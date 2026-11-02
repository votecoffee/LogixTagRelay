namespace LogixTagRelay;

/// <summary>Owns transport attempts and preserves dirty values through bounded-backoff recovery.</summary>
public sealed class ExplicitContractSession : IRelaySession
{
    private readonly Func<CancellationToken, ValueTask<IExplicitTransport>> connect;
    private readonly RecoveryOptions recovery;
    private int running, suspended;
    /// <summary>Transport kind used by the session.</summary>
    public SessionKind Kind => SessionKind.Explicit;
    private readonly SyncConfiguration configuration;
    /// <summary>A defensive copy of the session&apos;s contract configuration.</summary>
    public SyncConfiguration Configuration => SyncConfiguration.FromJson(configuration.ToJson());
    /// <summary>The application&apos;s live generated model instance shared with this session.</summary>
    public IPlcModel Model { get; }
    /// <summary>Communication state, freshness, faults, retry and interval counters for this session.</summary>
    public SessionDiagnostics Diagnostics { get; } = new();
    /// <summary>Explicit heartbeat diagnostics, or null when heartbeat is not configured.</summary>
    public SessionDiagnostics? HeartbeatDiagnostics => configuration.Heartbeat is null ? null : Diagnostics;
    /// <summary>Creates a recoverable explicit contract session. Each connection attempt must receive a newly owned transport.</summary>
    /// <param name="configuration">Matching generated contract configuration.</param>
    /// <param name="model">Live application model instance used by synchronization.</param>
    /// <param name="connect">Factory returning a newly owned transport and honoring cancellation on each attempt.</param>
    /// <param name="recovery">Optional automatic recovery and uncertain-write policy; defaults apply when null.</param>
    public ExplicitContractSession(SyncConfiguration configuration, IPlcModel model,
        Func<CancellationToken, ValueTask<IExplicitTransport>> connect, RecoveryOptions? recovery = null)
    {
        ModelValidation.Validate(configuration, model);
        this.configuration = SyncConfiguration.FromJson(configuration.ToJson()); Model = model;
        this.connect = connect; this.recovery = recovery ?? new(); this.recovery.Validate();
    }
    /// <summary>Explicitly permits retained application edits to be transmitted after write suspension; reconcile fresh PLC readback first.</summary>
    public void ResumeWrites() { Volatile.Write(ref suspended, 0); Diagnostics.Suspend(false); }
    /// <summary>Runs until cancellation or a terminal fault. Cancel and await completion before disposing owned resources.</summary>
    /// <param name="cancellationToken">Cancels this operation. Caller cancellation remains cancellation rather than a fabricated communication fault.</param>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref running, 1) != 0) throw new InvalidOperationException("Session is already running.");
        int attempt = 0; TriggerResult? receipt = null;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                Diagnostics.BeginAttempt();
                try
                {
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken); timeout.CancelAfter(recovery.ConnectionTimeoutMs);
                    IExplicitTransport transport;
                    try { transport = await connect(timeout.Token).ConfigureAwait(false); }
                    catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { throw PlcErrors.Connect(new TimeoutException("Explicit connection timed out."), configuration); }
                    catch (Exception error) when (PlcErrors.IsCommunication(error) && error is not PlcCommunicationException) { throw PlcErrors.Connect(error, configuration); }
                    await using var owned = transport;
                    var session = new ExplicitSession(configuration, Model, transport, Diagnostics);
                    try { await session.ConnectAsync(timeout.Token).ConfigureAwait(false); }
                    catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { throw PlcErrors.Connect(new TimeoutException("Explicit connection timed out."), configuration); }
                    timeout.CancelAfter(Timeout.Infinite);
                    await session.RestoreTriggerStateAsync(receipt, cancellationToken).ConfigureAwait(false);
                    try { await session.RunScheduledAsync(() => Volatile.Read(ref suspended) == 0, () => attempt = 0, cancellationToken).ConfigureAwait(false); }
                    finally { receipt = session.LastTriggerResult ?? receipt; }
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
            Volatile.Write(ref running, 0);
        }
    }
}
