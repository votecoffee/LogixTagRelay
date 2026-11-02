namespace LogixTagRelay;

/// <summary>Owns transport attempts and preserves dirty values through bounded-backoff recovery.</summary>
public sealed class ExplicitContractSession : IRelaySession
{
    private readonly Func<CancellationToken, ValueTask<IExplicitTransport>> connect;
    private readonly RecoveryOptions recovery;
    private int running, suspended;
    public SessionKind Kind => SessionKind.Explicit;
    private readonly SyncConfiguration configuration;
    public SyncConfiguration Configuration => SyncConfiguration.FromJson(configuration.ToJson());
    public IPlcModel Model { get; }
    public SessionDiagnostics Diagnostics { get; } = new();
    public SessionDiagnostics? HeartbeatDiagnostics => configuration.Heartbeat is null ? null : Diagnostics;
    public ExplicitContractSession(SyncConfiguration configuration, IPlcModel model,
        Func<CancellationToken, ValueTask<IExplicitTransport>> connect, RecoveryOptions? recovery = null)
    {
        ModelValidation.Validate(configuration, model);
        this.configuration = SyncConfiguration.FromJson(configuration.ToJson()); Model = model;
        this.connect = connect; this.recovery = recovery ?? new(); this.recovery.Validate();
    }
    public void ResumeWrites() { Volatile.Write(ref suspended, 0); Diagnostics.Suspend(false); }
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
