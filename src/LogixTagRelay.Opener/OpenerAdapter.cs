namespace LogixTagRelay.Opener;

/// <summary>Recoverable Windows x64 adapter with a private native worker process.</summary>
public sealed class OpenerAdapter : IAsyncDisposable, IRelaySession
{
    private readonly ImplicitContractSession session;
    private string? nic;
    private uint serial;
    private int running;
    private bool disposed;
    public SessionKind Kind => SessionKind.Implicit;
    public SyncConfiguration Configuration => session.Configuration;
    public IPlcModel Model => session.Model;
    public SessionDiagnostics Diagnostics => session.Diagnostics;
    public OpenerAdapter(SyncConfiguration config, IPlcModel model, string? nicNameOrIndex = null, uint deviceSerialNumber = 1, RecoveryOptions? recovery = null)
    {
        nic = nicNameOrIndex; serial = deviceSerialNumber;
        session = new(config, model, ct => WorkerTransport.ConnectAsync(Configuration, nic ?? throw new InvalidOperationException("Choose an adapter NIC."), serial, ct), recovery);
    }
    public static int NativeAbiVersion => NativeBridge.AbiVersion();
    /// <summary>Exercises the packaged child entry point and native ABI without opening any network ports.</summary>
    public static Task<int> ProbeWorkerAsync(CancellationToken cancellationToken = default) => WorkerTransport.ProbeAsync(cancellationToken);
    public void ResumeWrites() => session.ResumeWrites();
    public Task RunAsync(CancellationToken cancellationToken) => RunAsync(nic ?? throw new InvalidOperationException("Choose an adapter NIC."), serial, cancellationToken);
    public async Task RunAsync(string nicNameOrIndex, uint deviceSerialNumber, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (Interlocked.Exchange(ref running, 1) != 0) throw new InvalidOperationException("Adapter is already running.");
        nic = nicNameOrIndex; serial = deviceSerialNumber;
        try { await session.RunAsync(cancellationToken).ConfigureAwait(false); }
        finally { Volatile.Write(ref running, 0); }
    }
    public ValueTask DisposeAsync()
    {
        if (Volatile.Read(ref running) != 0) throw new InvalidOperationException("Cancel and await RunAsync before disposing the adapter.");
        disposed = true; return ValueTask.CompletedTask;
    }
}
