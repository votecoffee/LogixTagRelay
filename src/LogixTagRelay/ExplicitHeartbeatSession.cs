using System.Diagnostics;

namespace LogixTagRelay;

/// <summary>Explicit-only liveness channel; it never reads or writes contract payload members.</summary>
public sealed class ExplicitHeartbeatSession
{
    private readonly SyncConfiguration config;
    private readonly Func<CancellationToken, ValueTask<IExplicitTransport>> connect;
    private readonly RecoveryOptions recovery;
    private int running;
    /// <summary>Communication state, freshness, faults, retry and interval counters for this session.</summary>
    public SessionDiagnostics Diagnostics { get; } = new();
    /// <summary>Creates an independent explicit-only BOOL heartbeat channel using the configured tag and toggle interval.</summary>
    /// <param name="configuration">Matching generated contract configuration.</param>
    /// <param name="connect">Factory returning a newly owned transport and honoring cancellation on each attempt.</param>
    /// <param name="recovery">Optional automatic recovery and uncertain-write policy; defaults apply when null.</param>
    public ExplicitHeartbeatSession(SyncConfiguration configuration, Func<CancellationToken, ValueTask<IExplicitTransport>> connect, RecoveryOptions? recovery = null)
    {
        config = SyncConfiguration.FromJson(configuration.ToJson());
        if (config.Heartbeat is null) throw new ArgumentException("A heartbeat BOOL tag must be configured.");
        this.connect = connect; this.recovery = recovery ?? new(); this.recovery.Validate();
    }
    /// <summary>Runs until cancellation or a terminal fault. Cancel and await completion before disposing owned resources.</summary>
    /// <param name="ct">Cancels this operation.</param>
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
