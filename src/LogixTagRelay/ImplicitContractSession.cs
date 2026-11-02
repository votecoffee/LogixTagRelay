using System.Diagnostics;

namespace LogixTagRelay;

public interface IImplicitTransport : IAsyncDisposable
{
    /// <summary>Returns the latest PLC image, or null while disconnected/idle. Never splits an image.</summary>
    ValueTask<byte[]?> ExchangeAsync(byte[] image, CancellationToken cancellationToken);
}

public sealed class ImplicitContractSession : IRelaySession
{
    private readonly Func<CancellationToken, ValueTask<IImplicitTransport>> connect;
    private readonly RecoveryOptions recovery;
    private int running, suspended;
    public SessionKind Kind => SessionKind.Implicit;
    private readonly SyncConfiguration configuration;
    public SyncConfiguration Configuration => SyncConfiguration.FromJson(configuration.ToJson());
    public IPlcModel Model { get; }
    public SessionDiagnostics Diagnostics { get; } = new();
    public ImplicitContractSession(SyncConfiguration configuration, IPlcModel model,
        Func<CancellationToken, ValueTask<IImplicitTransport>> connect, RecoveryOptions? recovery = null)
    {
        ModelValidation.Validate(configuration, model);
        if (configuration.Implicit is null) throw new ArgumentException("Implicit settings are required.");
        this.configuration = SyncConfiguration.FromJson(configuration.ToJson()); Model = model;
        this.connect = connect; this.recovery = recovery ?? new(); this.recovery.Validate();
    }
    public void ResumeWrites() { Volatile.Write(ref suspended, 0); Diagnostics.Suspend(false); }
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref running, 1) != 0) throw new InvalidOperationException("Session is already running.");
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
                    catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { throw new TimeoutException("Implicit worker connection timed out."); }
                    await using var owned = transport;
                    var outgoing = codec.BeginResynchronization(); bool received = false;
                    long began = Stopwatch.GetTimestamp(), lastFresh = began; Diagnostics.SetState(SessionState.AwaitingBaseline);
                    int interval = configuration.Implicit!.RpiMs;
                    using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(interval));
                    do
                    {
                        long cycle = Stopwatch.GetTimestamp();
                        var incoming = await transport.ExchangeAsync(outgoing, cancellationToken).ConfigureAwait(false);
                        if (incoming is null && received) throw new IOException("PLC disconnected or entered idle; a fresh baseline is required.");
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
                            throw new TimeoutException("PLC baseline handshake timed out. Check connection and regenerate PLC imports with resynchronization support.");
                        if (received && Stopwatch.GetElapsedTime(lastFresh).TotalMilliseconds > recovery.StaleTimeoutMs)
                            throw new IOException("PLC stopped completing fresh implicit exchanges.");
                    } while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false));
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { break; }
                catch (Exception error)
                {
                    Diagnostics.Failure(error); foreach (var variable in Model.Variables) variable.MarkBad();
                    if (!recovery.Retry(error, ++attempt)) { Diagnostics.SetState(SessionState.Faulted); throw; }
                    if (recovery.PendingWrites == PendingWritePolicy.Suspend) Volatile.Write(ref suspended, 1);
                    Diagnostics.Recover(Volatile.Read(ref suspended) != 0);
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
