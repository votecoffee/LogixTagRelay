namespace LogixTagRelay;

public sealed record TriggerResult(int Request, bool Succeeded, Exception? Error, bool NotificationsDelivered);

public interface IExplicitTransport : IAsyncDisposable
{
    ValueTask<object> ReadAsync(string path, PlcType type, CancellationToken cancellationToken);
    ValueTask WriteAsync(string path, PlcType type, object value, CancellationToken cancellationToken);
    ValueTask<object> ReadAsync(FieldMap field, CancellationToken cancellationToken) => ReadAsync(field.Path, field.Type, cancellationToken);
    ValueTask WriteAsync(FieldMap field, object value, CancellationToken cancellationToken) => WriteAsync(field.Path, field.Type, value, cancellationToken);
}

/// <summary>Single serialized group. Owns no transport lifetime; the caller disposes its transport.</summary>
public sealed class ExplicitSession
{
    private readonly SyncConfiguration config;
    private readonly IPlcModel model;
    private readonly IExplicitTransport transport;
    private readonly SemaphoreSlim cycleGate = new(1, 1);
    private int running;
    private int lastObservedTrigger;
    private TriggerResult? lastTriggerResult;
    private readonly SemaphoreSlim triggerGate = new(1, 1);
    public TriggerResult? LastTriggerResult => Volatile.Read(ref lastTriggerResult);
    public ExplicitSession(SyncConfiguration config, IPlcModel model, IExplicitTransport transport)
    {
        ModelValidation.Validate(config, model);
        this.config = config; this.model = model; this.transport = transport;
    }
    public async Task SynchronizeOnceAsync(CancellationToken cancellationToken = default)
    {
        await cycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Stage ALL reads before publishing them. No multi-tag PLC atomicity is implied.
            var reads = new List<(IPlcVariable Variable, object Value)>();
            foreach (var variable in model.Variables)
            {
                var value = await transport.ReadAsync(variable.Map, cancellationToken).ConfigureAwait(false);
                if (variable.Map.Type == PlcType.STRING) PlcTypes.ValidateString((string)value, variable.Map.StringCapacity);
                reads.Add((variable, value));
            }
            var time = DateTimeOffset.UtcNow;
            foreach (var (variable, value) in reads) variable.Observe(value, time);
            foreach (var variable in model.Variables.Where(v => v.Map.Direction == Direction.AppToPlc && v.Dirty))
            {
                var captured = variable.Capture();
                try
                {
                    await transport.WriteAsync(variable.Map, captured.Value, cancellationToken).ConfigureAwait(false);
                    variable.AcceptWrite(captured.Version, DateTimeOffset.UtcNow);
                }
                catch { variable.MarkBad(); throw; }
            }
        }
        catch
        {
            foreach (var variable in model.Variables) variable.MarkBad();
            throw;
        }
        finally { cycleGate.Release(); }
    }
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref running, 1) != 0) throw new InvalidOperationException("Session is already running.");
        var options = config.Explicit ?? new ExplicitOptions();
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(options.TriggerTag is null ? options.IntervalMs : options.TriggerPollMs));
            // First cycle is immediate; subsequent cycles use a fixed cadence with no overlap.
            do
            {
                if (options.TriggerTag is null) await SynchronizeOnceAsync(cancellationToken).ConfigureAwait(false);
                else
                {
                    await PollTriggerOnceAsync(cancellationToken).ConfigureAwait(false);
                }
            } while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false));
        }
        finally { Volatile.Write(ref running, 0); }
    }
    public async Task<TriggerResult?> PollTriggerOnceAsync(CancellationToken cancellationToken = default)
    {
        var options = config.Explicit ?? throw new InvalidOperationException("A DINT trigger must be configured.");
        if (options.TriggerTag is null) throw new InvalidOperationException("A DINT trigger must be configured.");
        await triggerGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            int request = (int)await transport.ReadAsync(options.TriggerTag, PlcType.DINT, cancellationToken).ConfigureAwait(false);
            if (request == lastObservedTrigger) return null;
            lastObservedTrigger = request; // Zero re-arms the edge detector but never executes a transfer.
            if (request == 0) return null;
            if (options.ResponseTag is not null)
                await transport.WriteAsync(options.ResponseTag, PlcType.DINT, 0, cancellationToken).ConfigureAwait(false);
            Exception? error = null;
            try
            {
                if (options.ResponseTag is not null && request == int.MinValue)
                    throw new ArgumentOutOfRangeException(nameof(request), "The minimum DINT cannot produce a positive DINT ACK; roll request values over before this boundary.");
                await SynchronizeOnceAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception failure) { error = failure; }

            var result = new TriggerResult(request, error is null, error, false);
            Volatile.Write(ref lastTriggerResult, result);
            // Response first, exact mirror last: the mirror is the completion marker.
            // A notification failure faults without replacing a possibly-delivered ACK with a NACK.
            if (options.ResponseTag is not null)
                await transport.WriteAsync(options.ResponseTag, PlcType.DINT, SignedResponse(request, error is null), cancellationToken).ConfigureAwait(false);
            if (options.MirrorTag is not null)
                await transport.WriteAsync(options.MirrorTag, PlcType.DINT, request, cancellationToken).ConfigureAwait(false);
            result = result with { NotificationsDelivered = true };
            Volatile.Write(ref lastTriggerResult, result);
            if (error != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error).Throw();
            return result;
        }
        finally { triggerGate.Release(); }
    }
    public static int SignedResponse(int request, bool succeeded)
    {
        if (request == 0) throw new ArgumentOutOfRangeException(nameof(request));
        long magnitude = Math.Abs((long)request);
        return checked((int)(succeeded ? magnitude : -magnitude));
    }
}
