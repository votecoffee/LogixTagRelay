using System.Diagnostics;

namespace LogixTagRelay;

/// <summary>Explicit-only liveness channel; it never reads or writes contract payload members.</summary>
public sealed class ExplicitHeartbeatSession
{
    private readonly SyncConfiguration config;
    private readonly Func<CancellationToken, ValueTask<IExplicitTransport>> connect;
    private readonly RecoveryOptions recovery;
    private int running;
    public SessionDiagnostics Diagnostics { get; } = new();
    public ExplicitHeartbeatSession(SyncConfiguration configuration, Func<CancellationToken, ValueTask<IExplicitTransport>> connect, RecoveryOptions? recovery = null)
    {
        config = SyncConfiguration.FromJson(configuration.ToJson());
        if (config.Heartbeat is null) throw new ArgumentException("A heartbeat BOOL tag must be configured.");
        this.connect = connect; this.recovery = recovery ?? new(); this.recovery.Validate();
    }
    public async Task RunAsync(CancellationToken ct)
    {
        if (Interlocked.Exchange(ref running, 1) != 0) throw new InvalidOperationException("Heartbeat is already running.");
        var options = config.Heartbeat!; int attempt = 0;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                Diagnostics.BeginAttempt();
                try
                {
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(recovery.ConnectionTimeoutMs);
                    IExplicitTransport transport;
                    try { transport = await connect(timeout.Token).ConfigureAwait(false); }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw PlcErrors.Connect(new TimeoutException("Heartbeat connection timed out."), config); }
                    catch (Exception error) when (PlcErrors.IsCommunication(error) && error is not PlcCommunicationException) { throw PlcErrors.Connect(error, config); }
                    await using var owned = transport;
                    try { await transport.ConnectAsync(timeout.Token).ConfigureAwait(false); }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw PlcErrors.Connect(new TimeoutException("Heartbeat connection timed out."), config); }
                    catch (Exception error) when (PlcErrors.IsCommunication(error) && error is not PlcCommunicationException) { throw PlcErrors.Connect(error, config); }
                    timeout.CancelAfter(Timeout.Infinite);
                    bool phase;
                    try { phase = (bool)await transport.ReadAsync(options.Tag, PlcType.BOOL, ct).ConfigureAwait(false); }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                    catch (Exception error) when (PlcErrors.IsCommunication(error)) { throw PlcErrors.Read(error, config, options.Tag, PlcOperation.ReadHeartbeat); }
                    Diagnostics.Heartbeat(phase, false); Diagnostics.Contact(); Diagnostics.SetState(SessionState.Running);
                    var schedule = new IntervalSchedule(options.IntervalMs, Diagnostics, immediate: false, heartbeat: true);
                    while (!ct.IsCancellationRequested)
                    {
                        await IntervalSchedule.WaitAsync(schedule, null, ct).ConfigureAwait(false);
                        if (!schedule.Due) continue;
                        long start = schedule.Begin(); bool next = !phase;
                        try
                        {
                            await transport.WriteAsync(options.Tag, PlcType.BOOL, next, ct).ConfigureAwait(false);
                            phase = next; Diagnostics.Heartbeat(phase, true); Diagnostics.Success(Stopwatch.GetElapsedTime(start), options.IntervalMs); attempt = 0;
                        }
                        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                        catch (Exception error) when (PlcErrors.IsCommunication(error)) { throw PlcErrors.Write(error, config, options.Tag, PlcOperation.WriteHeartbeat); }
                        finally { schedule.Complete(); }
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
                catch (Exception error)
                {
                    Diagnostics.Failure(error);
                    if (!recovery.Retry(error, ++attempt)) { Diagnostics.SetState(SessionState.Faulted); throw; }
                    Diagnostics.Recover(false, recovery.Delay(attempt)); await Task.Delay(recovery.Delay(attempt), ct).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        finally { if (Diagnostics.Snapshot.State != SessionState.Faulted) Diagnostics.SetState(SessionState.Stopped); Volatile.Write(ref running, 0); }
    }
}
