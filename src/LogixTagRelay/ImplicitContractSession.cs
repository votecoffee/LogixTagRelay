using System.Diagnostics;

namespace LogixTagRelay;

/// <summary>Owned full-image exchange transport for one bounded implicit contract.</summary>
public interface IImplicitTransport : IAsyncDisposable
{
    /// <summary>Returns the latest PLC image, or null while disconnected/idle. Never splits an image.</summary>
    /// <param name="image">Complete symmetric relay image including its header.</param>
    /// <param name="cancellationToken">Cancels this operation. Caller cancellation remains cancellation rather than a fabricated communication fault.</param>
    ValueTask<byte[]?> ExchangeAsync(byte[] image, CancellationToken cancellationToken);
}

/// <summary>Recoverable implicit image session with independent optional explicit heartbeat and suspended uncertain writes.</summary>
public sealed class ImplicitContractSession : IRelaySession
{
    private readonly Func<CancellationToken, ValueTask<IImplicitTransport>> connect;
    private readonly RecoveryOptions recovery;
    private int running, suspended;
    private readonly ExplicitHeartbeatSession? heartbeat;
    /// <summary>Transport kind used by the session.</summary>
    public SessionKind Kind => SessionKind.Implicit;
    private readonly SyncConfiguration configuration;
    /// <summary>A defensive copy of the session&apos;s contract configuration.</summary>
    public SyncConfiguration Configuration => SyncConfiguration.FromJson(configuration.ToJson());
    /// <summary>The application&apos;s live generated model instance shared with this session.</summary>
    public IPlcModel Model { get; }
    /// <summary>Communication state, freshness, faults, retry and interval counters for this session.</summary>
    public SessionDiagnostics Diagnostics { get; } = new();
    /// <summary>Explicit heartbeat diagnostics, or null when heartbeat is not configured.</summary>
    public SessionDiagnostics? HeartbeatDiagnostics => heartbeat?.Diagnostics;
    /// <summary>Recoverable implicit image session with independent optional explicit heartbeat and suspended uncertain writes.</summary>
    /// <param name="configuration">Matching generated contract configuration.</param>
    /// <param name="model">Live application model instance used by synchronization.</param>
    /// <param name="connect">Factory returning a newly owned transport and honoring cancellation on each attempt.</param>
    /// <param name="recovery">Optional automatic recovery and uncertain-write policy; defaults apply when null.</param>
    /// <param name="heartbeatConnect">Optional newly owned explicit transport factory, required when an implicit contract enables heartbeat.</param>
    public ImplicitContractSession(SyncConfiguration configuration, IPlcModel model,
        Func<CancellationToken, ValueTask<IImplicitTransport>> connect, RecoveryOptions? recovery = null,
        Func<CancellationToken, ValueTask<IExplicitTransport>>? heartbeatConnect = null)
    {
        ModelValidation.Validate(configuration, model);
        if (configuration.Implicit is null) throw new ArgumentException("Implicit settings are required.");
        this.configuration = SyncConfiguration.FromJson(configuration.ToJson()); Model = model;
        this.connect = connect; this.recovery = recovery ?? new(); this.recovery.Validate();
        if (configuration.Heartbeat is not null)
        {
            heartbeat = new(this.configuration, heartbeatConnect ?? throw new ArgumentException("An implicit contract heartbeat requires an explicit transport factory."), this.recovery);
            heartbeat.Diagnostics.Changed += (_, report) => Diagnostics.HeartbeatReport(report);
        }
    }
    /// <summary>Explicitly permits retained application edits to be transmitted after write suspension; reconcile fresh PLC readback first.</summary>
    public void ResumeWrites() { Volatile.Write(ref suspended, 0); Diagnostics.Suspend(false); }
    /// <summary>Runs the implicit channel and optional explicit heartbeat with independent recovery until cancellation.</summary>
    /// <remarks>A terminal channel fault is visible immediately in diagnostics; the combined task reports it after the other channel stops.</remarks>
    /// <param name="cancellationToken">Cancels this operation. Caller cancellation remains cancellation rather than a fabricated communication fault.</param>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref running, 1) != 0) throw new InvalidOperationException("Session is already running.");
        try
        {
            var data = RunDataAsync(cancellationToken);
            if (heartbeat is not null) await Task.WhenAll(data, heartbeat.RunAsync(cancellationToken)).ConfigureAwait(false);
            else await data.ConfigureAwait(false);
        }
        finally { Volatile.Write(ref running, 0); }
    }
    private async Task RunDataAsync(CancellationToken cancellationToken)
    {
        int attempt = 0;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                Diagnostics.BeginAttempt(); var codec = new ImplicitImage(configuration, Model);
                try
                {
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken); timeout.CancelAfter(recovery.ConnectionTimeoutMs);
                    IImplicitTransport transport;
                    try { transport = await connect(timeout.Token).ConfigureAwait(false); }
                    catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { throw PlcErrors.Connect(new TimeoutException("Implicit worker connection timed out."), configuration, PlcOperation.StartWorker); }
                    catch (Exception error) when (PlcErrors.IsCommunication(error) && error is not PlcCommunicationException) { throw PlcErrors.Connect(error, configuration, PlcOperation.StartWorker); }
                    await using var owned = transport; timeout.CancelAfter(Timeout.Infinite);
                    var outgoing = codec.BeginResynchronization(); bool received = false;
                    long began = Stopwatch.GetTimestamp(), lastFresh = began; Diagnostics.SetState(SessionState.AwaitingBaseline);
                    int interval = configuration.Implicit!.RpiMs;
                    var schedule = new IntervalSchedule(interval, Diagnostics);
                    do
                    {
                        long cycle = schedule.Begin();
                        byte[]? incoming;
                        try { incoming = await transport.ExchangeAsync(outgoing, cancellationToken).ConfigureAwait(false); }
                        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                        catch (Exception error) when (PlcErrors.IsCommunication(error)) { throw error is PlcCommunicationException ? error : PlcErrors.Connect(error, configuration, PlcOperation.ExchangeImage); }
                        if (incoming is null && received) throw PlcErrors.Connect(new IOException("PLC disconnected or entered idle; a fresh baseline is required."), configuration, PlcOperation.ExchangeImage);
                        if (incoming is not null)
                        {
                            Diagnostics.Contact();
                            // Initial zero transport buffers do not constitute PLC snapshots.
                            if (incoming.Any(b => b != 0) && codec.ReceivePlcToApp(incoming, DateTimeOffset.UtcNow))
                            {
                                received = true; attempt = 0; lastFresh = Stopwatch.GetTimestamp();
                                Diagnostics.Success(Stopwatch.GetElapsedTime(cycle), interval);
                                outgoing = codec.BuildAppToPlc(Volatile.Read(ref suspended) == 0);
                            }
                        }
                        if (!received && Stopwatch.GetElapsedTime(began).TotalMilliseconds > recovery.ConnectionTimeoutMs)
                            throw PlcErrors.Connect(new TimeoutException("PLC baseline handshake timed out. Check the PLC connection and generated resynchronization routine."), configuration, PlcOperation.ImplicitHandshake);
                        if (received && Stopwatch.GetElapsedTime(lastFresh).TotalMilliseconds > recovery.StaleTimeoutMs)
                            throw PlcErrors.Connect(new IOException("PLC stopped completing fresh implicit exchanges."), configuration, PlcOperation.ExchangeImage);
                        schedule.Complete(); await IntervalSchedule.WaitAsync(schedule, null, cancellationToken).ConfigureAwait(false);
                    } while (!cancellationToken.IsCancellationRequested);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { break; }
                catch (Exception error)
                {
                    Diagnostics.Failure(error); foreach (var variable in Model.Variables) variable.MarkBad();
                    if (!recovery.Retry(error, ++attempt)) { Diagnostics.SetState(SessionState.Faulted); throw; }
                    if (recovery.PendingWrites == PendingWritePolicy.Suspend) Volatile.Write(ref suspended, 1);
                    Diagnostics.Recover(Volatile.Read(ref suspended) != 0, recovery.Delay(attempt));
                    await Task.Delay(recovery.Delay(attempt), cancellationToken).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        finally
        {
            foreach (var variable in Model.Variables) variable.MarkBad();
            if (recovery.PendingWrites == PendingWritePolicy.Suspend && Model.Variables.Any(v => v.Dirty)) { Volatile.Write(ref suspended, 1); Diagnostics.Suspend(true); }
            if (Diagnostics.Snapshot.State != SessionState.Faulted) Diagnostics.SetState(SessionState.Stopped);
        }
    }
}
