using System.Diagnostics;

namespace LogixTagRelay;

/// <summary>Result of one nonzero DINT-triggered transfer and its optional notification writes.</summary>
/// <param name="Request">Nonzero DINT request value handled by this transfer.</param>
/// <param name="Succeeded">Whether the requested data transfer succeeded.</param>
/// <param name="Error">Failure details, or null on success.</param>
/// <param name="NotificationsDelivered">Whether all configured handled and ACK/NACK writes completed.</param>
public sealed record TriggerResult(int Request, bool Succeeded, Exception? Error, bool NotificationsDelivered);

/// <summary>Owned explicit read/write connection. Implementations must honor cancellation and preserve scalar CLR types.</summary>
public interface IExplicitTransport : IAsyncDisposable
{
    /// <summary>Checks the endpoint before tag access. Default custom transports require no separate connection operation.</summary>
    /// <param name="cancellationToken">Cancels this operation. Caller cancellation remains cancellation rather than a fabricated communication fault.</param>
    ValueTask ConnectAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;
    /// <summary>Reads one scalar in its matching CLR type. Use the FieldMap overload for STRING capacity metadata.</summary>
    /// <param name="path">Fully qualified Logix scalar path.</param>
    /// <param name="type">Supported PLC scalar type.</param>
    /// <param name="cancellationToken">Cancels this operation. Caller cancellation remains cancellation rather than a fabricated communication fault.</param>
    ValueTask<object> ReadAsync(string path, PlcType type, CancellationToken cancellationToken);
    /// <summary>Writes one addressed scalar. Use the FieldMap overload for STRING capacity metadata; completion is not a multi-tag transaction.</summary>
    /// <param name="path">Fully qualified Logix scalar path.</param>
    /// <param name="type">Supported PLC scalar type.</param>
    /// <param name="value">Typed scalar or timestamp value required by this operation.</param>
    /// <param name="cancellationToken">Cancels this operation. Caller cancellation remains cancellation rather than a fabricated communication fault.</param>
    ValueTask WriteAsync(string path, PlcType type, object value, CancellationToken cancellationToken);
    /// <summary>Reads one scalar in its matching CLR type. Use the FieldMap overload for STRING capacity metadata.</summary>
    /// <param name="field">Scalar mapping, including STRING capacity when applicable.</param>
    /// <param name="cancellationToken">Cancels this operation. Caller cancellation remains cancellation rather than a fabricated communication fault.</param>
    ValueTask<object> ReadAsync(FieldMap field, CancellationToken cancellationToken) => ReadAsync(field.Path, field.Type, cancellationToken);
    /// <summary>Writes one addressed scalar. Use the FieldMap overload for STRING capacity metadata; completion is not a multi-tag transaction.</summary>
    /// <param name="field">Scalar mapping, including STRING capacity when applicable.</param>
    /// <param name="value">Typed scalar or timestamp value required by this operation.</param>
    /// <param name="cancellationToken">Cancels this operation. Caller cancellation remains cancellation rather than a fabricated communication fault.</param>
    ValueTask WriteAsync(FieldMap field, object value, CancellationToken cancellationToken) => WriteAsync(field.Path, field.Type, value, cancellationToken);
    /// <summary>Returns values in field order. Native UDT groups may share a snapshot; separate groups are not an atomic PLC transaction.</summary>
    /// <param name="fields">Ordered scalar mappings to validate, encode or read.</param>
    /// <param name="groups">Whole-UDT root read groups associated with the scalar fields.</param>
    /// <param name="cancellationToken">Cancels this operation. Caller cancellation remains cancellation rather than a fabricated communication fault.</param>
    async ValueTask<IReadOnlyList<object>> ReadBatchAsync(IReadOnlyList<FieldMap> fields, IReadOnlyList<ExplicitReadGroup> groups, CancellationToken cancellationToken)
    {
        var values = new List<object>(fields.Count);
        foreach (var field in fields)
        {
            try { values.Add(await ReadAsync(field, cancellationToken).ConfigureAwait(false)); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception error) when (PlcErrors.IsCommunication(error))
            { throw error is PlcCommunicationException ? error : new PlcTagReadException(field.Path, null, null, error); }
        }
        return values;
    }
}

/// <summary>Single serialized group. Owns no transport lifetime; the caller disposes its transport.</summary>
public sealed class ExplicitSession
{
    private readonly SyncConfiguration config;
    private readonly IReadOnlyList<IPlcVariable> variables;
    private readonly IExplicitTransport transport;
    private readonly SemaphoreSlim cycleGate = new(1, 1);
    private int running;
    private int writesEnabled = 1;
    /// <summary>Whether data updates may transmit pending application edits; reads and explicit heartbeat remain active.</summary>
    public bool WritesEnabled { get => Volatile.Read(ref writesEnabled) != 0; set => Volatile.Write(ref writesEnabled, value ? 1 : 0); }
    /// <summary>Communication state, freshness, faults, retry and interval counters for this session.</summary>
    public SessionDiagnostics Diagnostics { get; }
    private int lastObservedTrigger;
    private TriggerResult? lastTriggerResult;
    private readonly HeartbeatState heartbeat;
    private bool connected, heartbeatInitialized;
    /// <summary>Most recent completed trigger result, or null before any transfer completes.</summary>
    public TriggerResult? LastTriggerResult => Volatile.Read(ref lastTriggerResult);
    /// <summary>Creates a serialized explicit data and heartbeat session over a caller-owned transport.</summary>
    /// <param name="config">Matching generated contract configuration.</param>
    /// <param name="model">Live application model instance used by synchronization.</param>
    /// <param name="transport">Explicit transport owned and disposed by the caller, not by the raw session.</param>
    /// <param name="diagnostics">Optional shared diagnostics instance; a new instance is created when null.</param>
    public ExplicitSession(SyncConfiguration config, IPlcModel model, IExplicitTransport transport, SessionDiagnostics? diagnostics = null)
    {
        Diagnostics = diagnostics ?? new();
        ModelValidation.Validate(config, model);
        this.config = SyncConfiguration.FromJson(config.ToJson()); variables = model.Variables.ToArray(); this.transport = transport; heartbeat = new(Diagnostics);
    }
    private async Task SynchronizeCoreAsync(CancellationToken cancellationToken)
    {
        long started = Stopwatch.GetTimestamp();
        try
        {
            // Stage ALL reads before publishing them. No multi-tag PLC atomicity is implied.
            var reads = new List<(IPlcVariable Variable, object Value)>();
            IReadOnlyList<object> values;
            try { values = await transport.ReadBatchAsync(config.Fields, config.Explicit?.ReadGroups ?? [], cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception error) when (PlcErrors.IsCommunication(error)) { throw PlcErrors.Read(error, config, "<read batch>"); }
            Diagnostics.Contact();
            if (values.Count != variables.Count) throw new FormatException("Explicit read batch does not match the contract.");
            for (int i = 0; i < variables.Count; i++)
            {
                var variable = variables[i]; var value = values[i];
                PlcTypes.Encode(variable.Map, value, new byte[variable.Map.WireBytes]);
                reads.Add((variable, value));
            }
            var time = DateTimeOffset.UtcNow;
            foreach (var (variable, value) in reads) variable.Observe(value, time);
            foreach (var variable in variables.Where(v => v.Map.CanWrite && WritesEnabled && !v.Map.Path.Equals(config.Heartbeat?.Tag, StringComparison.OrdinalIgnoreCase)))
            {
                var captured = variable.CaptureForMerge();
                if (!captured.Dirty) continue;
                try
                {
                    // A fresh PLC read already equal to the desired member needs no network write.
                    if (!Equals(variable.PlcValue, captured.Value))
                        await WriteFieldAsync(variable.Map, captured.Value, cancellationToken).ConfigureAwait(false);
                    variable.AcceptWrite(captured.Version, DateTimeOffset.UtcNow);
                }
                catch { variable.MarkBad(); throw; }
            }
            Diagnostics.Success(Stopwatch.GetElapsedTime(started), config.Explicit?.IntervalMs ?? 100);
        }
        catch (Exception error)
        {
            if (error is not OperationCanceledException || !cancellationToken.IsCancellationRequested) Diagnostics.Failure(error);
            foreach (var variable in variables) variable.MarkBad();
            throw;
        }
    }
    /// <summary>Runs until cancellation or a terminal fault. Cancel and await completion before disposing owned resources.</summary>
    /// <param name="cancellationToken">Cancels this operation. Caller cancellation remains cancellation rather than a fabricated communication fault.</param>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref running, 1) != 0) throw new InvalidOperationException("Session is already running.");
        try { await RunScheduledAsync(() => WritesEnabled, null, cancellationToken).ConfigureAwait(false); }
        finally { if (Diagnostics.Snapshot.State != SessionState.Faulted) Diagnostics.SetState(SessionState.Stopped); Volatile.Write(ref running, 0); }
    }
    internal async Task RunScheduledAsync(Func<bool> writesAllowed, Action? successfulTick, CancellationToken ct)
    {
        await ConnectAsync(ct).ConfigureAwait(false);
        if (config.Heartbeat is not null && !heartbeatInitialized) await HeartbeatOnceAsync(ct).ConfigureAwait(false);
        var options = config.Explicit ?? new();
        var updates = new IntervalSchedule(options.TriggerTag is null ? options.IntervalMs : options.TriggerPollMs, Diagnostics);
        var heartbeats = config.Heartbeat is { } h ? new IntervalSchedule(h.IntervalMs, Diagnostics, immediate: false, heartbeat: true) : null;
        while (!ct.IsCancellationRequested)
        {
            WritesEnabled = writesAllowed();
            if (updates.Due)
            {
                updates.Begin();
                try
                {
                    if (options.TriggerTag is null || !WritesEnabled) await SynchronizeOnceAsync(ct).ConfigureAwait(false);
                    else await PollTriggerOnceAsync(ct).ConfigureAwait(false);
                }
                finally { updates.Complete(); }
            }
            if (heartbeats?.Due == true)
            {
                heartbeats.Begin();
                try { await HeartbeatOnceAsync(ct).ConfigureAwait(false); }
                finally { heartbeats.Complete(); }
            }
            Diagnostics.SetState(SessionState.Running); successfulTick?.Invoke();
            await IntervalSchedule.WaitAsync(updates, heartbeats, ct).ConfigureAwait(false);
        }
    }
    /// <summary>Polls the DINT trigger and handles a changed nonzero request, including optional handled and ACK/NACK writes.</summary>
    /// <param name="cancellationToken">Cancels this operation. Caller cancellation remains cancellation rather than a fabricated communication fault.</param>
    public async Task<TriggerResult?> PollTriggerOnceAsync(CancellationToken cancellationToken = default)
    {
        var options = config.Explicit ?? throw new InvalidOperationException("A DINT trigger must be configured.");
        if (options.TriggerTag is null) throw new InvalidOperationException("A DINT trigger must be configured.");
        await EnterAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);
            int request = (int)await ReadTagAsync(options.TriggerTag, PlcType.DINT, cancellationToken, PlcOperation.ReadTrigger).ConfigureAwait(false);
            Diagnostics.Contact();
            if (request == lastObservedTrigger) return null;
            lastObservedTrigger = request; // Zero re-arms the edge detector but never executes a transfer.
            if (request == 0) return null;
            if (options.ResponseTag is not null)
                await WriteTagAsync(options.ResponseTag, PlcType.DINT, 0, cancellationToken).ConfigureAwait(false);
            Exception? error = null;
            try
            {
                if (options.ResponseTag is not null && request == int.MinValue)
                    throw new ArgumentOutOfRangeException(nameof(request), "The minimum DINT cannot produce a positive DINT ACK; roll request values over before this boundary.");
                await SynchronizeCoreAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception failure) { error = failure; }

            var result = new TriggerResult(request, error is null, error, false);
            Volatile.Write(ref lastTriggerResult, result); Diagnostics.Trigger(result);
            // Response first, exact mirror last: the mirror is the completion marker.
            // A notification failure faults without replacing a possibly-delivered ACK with a NACK.
            if (options.ResponseTag is not null)
                await WriteTagAsync(options.ResponseTag, PlcType.DINT, SignedResponse(request, error is null), cancellationToken).ConfigureAwait(false);
            if (options.MirrorTag is not null)
                await WriteTagAsync(options.MirrorTag, PlcType.DINT, request, cancellationToken).ConfigureAwait(false);
            result = result with { NotificationsDelivered = true };
            Volatile.Write(ref lastTriggerResult, result); Diagnostics.Trigger(result);
            if (error != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error).Throw();
            return result;
        }
        catch (Exception error)
        {
            if (error is not OperationCanceledException || !cancellationToken.IsCancellationRequested) Diagnostics.Failure(error);
            foreach (var variable in variables) variable.MarkBad(); throw;
        }
        finally { cycleGate.Release(); }
    }
    internal async Task RestoreTriggerStateAsync(TriggerResult? receipt, CancellationToken ct)
    {
        var options = config.Explicit;
        if (options?.TriggerTag is null) return;
        if (receipt is { NotificationsDelivered: false } && (int)await ReadTagAsync(options.TriggerTag, PlcType.DINT, ct).ConfigureAwait(false) == receipt.Request)
        {
            // Re-deliver an established result, never re-execute its data writes to repair a receipt.
            if (options.ResponseTag is not null) await WriteTagAsync(options.ResponseTag, PlcType.DINT, SignedResponse(receipt.Request, receipt.Succeeded), ct).ConfigureAwait(false);
            if (options.MirrorTag is not null) await WriteTagAsync(options.MirrorTag, PlcType.DINT, receipt.Request, ct).ConfigureAwait(false);
            receipt = receipt with { NotificationsDelivered = true }; Volatile.Write(ref lastTriggerResult, receipt); Diagnostics.Trigger(receipt);
            lastObservedTrigger = receipt.Request;
        }
        if (options.MirrorTag is not null) lastObservedTrigger = (int)await ReadTagAsync(options.MirrorTag, PlcType.DINT, ct).ConfigureAwait(false);
    }
    private async Task EnterAsync(CancellationToken ct)
    {
        if (!cycleGate.Wait(0)) { Diagnostics.Overlap(); await cycleGate.WaitAsync(ct).ConfigureAwait(false); }
    }
    private async ValueTask EnsureConnectedAsync(CancellationToken ct)
    {
        if (connected) return;
        try { await transport.ConnectAsync(ct).ConfigureAwait(false); connected = true; }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception error) when (PlcErrors.IsCommunication(error)) { throw error is PlcCommunicationException ? error : PlcErrors.Connect(error, config); }
    }
    /// <summary>Checks the endpoint before tag access. Default custom transports require no separate connection operation.</summary>
    /// <param name="ct">Cancels this operation.</param>
    public async Task ConnectAsync(CancellationToken ct = default)
    {
        await EnterAsync(ct).ConfigureAwait(false);
        try { await EnsureConnectedAsync(ct).ConfigureAwait(false); }
        catch (Exception error) { if (error is not OperationCanceledException || !ct.IsCancellationRequested) Diagnostics.Failure(error); throw; }
        finally { cycleGate.Release(); }
    }
    /// <summary>Reads the contract and writes only dirty writable members. New edits during a write remain pending.</summary>
    /// <param name="cancellationToken">Cancels this operation. Caller cancellation remains cancellation rather than a fabricated communication fault.</param>
    public async Task SynchronizeOnceAsync(CancellationToken cancellationToken = default)
    {
        await EnterAsync(cancellationToken).ConfigureAwait(false);
        try { await EnsureConnectedAsync(cancellationToken).ConfigureAwait(false); await SynchronizeCoreAsync(cancellationToken).ConfigureAwait(false); }
        catch (Exception error) { if (error is not OperationCanceledException || !cancellationToken.IsCancellationRequested) Diagnostics.Failure(error); throw; }
        finally { cycleGate.Release(); }
    }
    /// <summary>Seeds the actual PLC BOOL on first use, then toggles it on each subsequent call, serialized with explicit data operations.</summary>
    /// <param name="ct">Cancels this operation.</param>
    public async Task<bool> HeartbeatOnceAsync(CancellationToken ct = default)
    {
        if (config.Heartbeat is not { } options) return false;
        await EnterAsync(ct).ConfigureAwait(false);
        try
        {
            await EnsureConnectedAsync(ct).ConfigureAwait(false);
            if (!heartbeatInitialized)
            {
                heartbeat.Seed((bool)await ReadTagAsync(options.Tag, PlcType.BOOL, ct, PlcOperation.ReadHeartbeat).ConfigureAwait(false));
                heartbeatInitialized = true; return false;
            }
            bool next = heartbeat.Prepare();
            await WriteTagAsync(options.Tag, PlcType.BOOL, next, ct, PlcOperation.WriteHeartbeat).ConfigureAwait(false);
            heartbeat.Confirm(next); Diagnostics.Contact(); return true;
        }
        catch (Exception error) { if (error is not OperationCanceledException || !ct.IsCancellationRequested) Diagnostics.Failure(error); throw; }
        finally { cycleGate.Release(); }
    }
    private async ValueTask<object> ReadTagAsync(string path, PlcType type, CancellationToken ct, PlcOperation operation = PlcOperation.ReadTag)
    {
        try { return await transport.ReadAsync(path, type, ct).ConfigureAwait(false); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception error) when (PlcErrors.IsCommunication(error)) { throw PlcErrors.Read(error, config, path, operation); }
    }
    private async ValueTask WriteTagAsync(string path, PlcType type, object value, CancellationToken ct, PlcOperation operation = PlcOperation.WriteResponse)
    {
        try { await transport.WriteAsync(path, type, value, ct).ConfigureAwait(false); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception error) when (PlcErrors.IsCommunication(error)) { throw PlcErrors.Write(error, config, path, operation); }
    }
    private async ValueTask WriteFieldAsync(FieldMap field, object value, CancellationToken ct)
    {
        try { await transport.WriteAsync(field, value, ct).ConfigureAwait(false); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception error) when (PlcErrors.IsCommunication(error)) { throw PlcErrors.Write(error, config, field.Path); }
    }
    /// <summary>Returns positive request magnitude for ACK or negative magnitude for NACK; rejects zero and throws on DINT overflow.</summary>
    /// <param name="request">Nonzero DINT request. Int32.MinValue cannot be represented as a positive DINT magnitude.</param>
    /// <param name="succeeded">True for positive ACK, false for negative NACK.</param>
    public static int SignedResponse(int request, bool succeeded)
    {
        if (request == 0) throw new ArgumentOutOfRangeException(nameof(request));
        long magnitude = Math.Abs((long)request);
        return checked((int)(succeeded ? magnitude : -magnitude));
    }
}
