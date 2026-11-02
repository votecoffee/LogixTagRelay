namespace LogixTagRelay.Opener;

/// <summary>Recoverable Windows x64 adapter with a private native worker process.</summary>
public sealed class OpenerAdapter : IAsyncDisposable, IRelaySession
{
    private readonly ImplicitContractSession session;
    private string? nic;
    private uint serial;
    private int running;
    private bool disposed;
    /// <summary>Transport kind used by the session.</summary>
    public SessionKind Kind => SessionKind.Implicit;
    /// <summary>A defensive copy of the session&apos;s contract configuration.</summary>
    public SyncConfiguration Configuration => session.Configuration;
    /// <summary>The application&apos;s live generated model instance shared with this session.</summary>
    public IPlcModel Model => session.Model;
    /// <summary>Communication state, freshness, faults, retry and interval counters for this session.</summary>
    public SessionDiagnostics Diagnostics => session.Diagnostics;
    /// <summary>Explicit heartbeat diagnostics, or null when heartbeat is not configured.</summary>
    public SessionDiagnostics? HeartbeatDiagnostics => session.HeartbeatDiagnostics;
    /// <summary>Creates a Windows x64 implicit adapter with a privately owned worker and optional independent explicit heartbeat.</summary>
    /// <param name="config">Matching generated contract configuration.</param>
    /// <param name="model">Live application model instance used by synchronization.</param>
    /// <param name="nicNameOrIndex">PC adapter NIC name or index; required before starting real implicit communication.</param>
    /// <param name="deviceSerialNumber">Unique adapter device serial number for the selected network.</param>
    /// <param name="recovery">Optional automatic recovery and uncertain-write policy; defaults apply when null.</param>
    public OpenerAdapter(SyncConfiguration config, IPlcModel model, string? nicNameOrIndex = null, uint deviceSerialNumber = 1, RecoveryOptions? recovery = null)
    {
        nic = nicNameOrIndex; serial = deviceSerialNumber;
        session = new(config, model, ct => WorkerTransport.ConnectAsync(Configuration, nic ?? throw new InvalidOperationException("Choose an adapter NIC."), serial, ct), recovery, ct => ValueTask.FromResult<IExplicitTransport>(new LogixTagRelay.Logix.LogixTransport(Configuration.Gateway, Configuration.Route)));
    }
    /// <summary>Loads the packaged native bridge and returns its ABI version; requires Windows x64 and native prerequisites.</summary>
    public static int NativeAbiVersion => NativeBridge.AbiVersion();
    /// <summary>Exercises the packaged child entry point and native ABI without opening any network ports.</summary>
    /// <param name="cancellationToken">Cancels this operation. Caller cancellation remains cancellation rather than a fabricated communication fault.</param>
    public static Task<int> ProbeWorkerAsync(CancellationToken cancellationToken = default) => WorkerTransport.ProbeAsync(cancellationToken);
    /// <summary>Explicitly permits retained application edits to be transmitted after write suspension; reconcile fresh PLC readback first.</summary>
    public void ResumeWrites() => session.ResumeWrites();
    /// <summary>Runs until cancellation or a terminal fault. Cancel and await completion before disposing owned resources.</summary>
    /// <param name="cancellationToken">Cancels this operation. Caller cancellation remains cancellation rather than a fabricated communication fault.</param>
    public Task RunAsync(CancellationToken cancellationToken) => RunAsync(nic ?? throw new InvalidOperationException("Choose an adapter NIC."), serial, cancellationToken);
    /// <summary>Runs until cancellation or a terminal fault. Cancel and await completion before disposing owned resources.</summary>
    /// <param name="nicNameOrIndex">PC adapter NIC name or index; required before starting real implicit communication.</param>
    /// <param name="deviceSerialNumber">Unique adapter device serial number for the selected network.</param>
    /// <param name="cancellationToken">Cancels this operation. Caller cancellation remains cancellation rather than a fabricated communication fault.</param>
    public async Task RunAsync(string nicNameOrIndex, uint deviceSerialNumber, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (Interlocked.Exchange(ref running, 1) != 0) throw new InvalidOperationException("Adapter is already running.");
        nic = nicNameOrIndex; serial = deviceSerialNumber;
        try { await session.RunAsync(cancellationToken).ConfigureAwait(false); }
        finally { Volatile.Write(ref running, 0); }
    }
    /// <summary>Releases owned resources. For running adapters, cancel and await RunAsync first.</summary>
    public ValueTask DisposeAsync()
    {
        if (Volatile.Read(ref running) != 0) throw new InvalidOperationException("Cancel and await RunAsync before disposing the adapter.");
        disposed = true; return ValueTask.CompletedTask;
    }
}
