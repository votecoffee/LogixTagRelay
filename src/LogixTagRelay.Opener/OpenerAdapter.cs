using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace LogixTagRelay.Opener;

/// <summary>Windows x64 OpENer adapter. Start is an explicit opt-in to bind EtherNet/IP ports.</summary>
public sealed class OpenerAdapter : IAsyncDisposable
{
    private readonly SyncConfiguration config;
    private readonly ImplicitImage image;
    private readonly SemaphoreSlim lifetime = new(1, 1);
    private bool started;
    private bool disposed;
    public OpenerAdapter(SyncConfiguration config, IPlcModel model)
    {
        if (config.Implicit is null) throw new ArgumentException("Implicit settings are required.");
        this.config = config; image = new(config, model);
    }
    public static int NativeAbiVersion => Native.AbiVersion();
    public async Task RunAsync(string nicNameOrIndex, uint deviceSerialNumber, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows() || !Environment.Is64BitProcess) throw new PlatformNotSupportedException("Windows x64 is required.");
        await lifetime.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (started) throw new InvalidOperationException("OpENer supports one start per process in this bridge.");
            var io = config.Implicit!;
            // Build first so uninitialized commands fail before opening any ports.
            byte[] frame = image.BuildAppToPlc();
            started = true;
            int result = Native.Start(nicNameOrIndex, config.AppToPlcBytes, config.PlcToAppBytes,
                io.PlcInputAssembly, io.PlcOutputAssembly, io.ConfigurationAssembly, deviceSerialNumber,
                (ushort)RandomNumberGenerator.GetInt32(1, ushort.MaxValue));
            if (result != 0) throw new IOException($"OpENer start failed ({result}). Check NIC and port availability. Restart the host process before another attempt.");
            try
            {
                Check(Native.SetImage(frame, frame.Length));
                var incoming = new byte[config.PlcToAppBytes];
                int generation = 0;
                bool received = false;
                using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(io.RpiMs));
                do
                {
                    int status = Native.GetImage(incoming, incoming.Length, out int observedGeneration);
                    if (status < 0) throw new IOException("OpENer network worker failed.");
                    if (received && status == 0) throw new IOException("PLC connection closed or entered idle. Restart the host before reconnecting.");
                    if (status == 1 && observedGeneration != generation)
                    {
                        image.ReceivePlcToApp(incoming, DateTimeOffset.UtcNow);
                        received = true;
                        generation = observedGeneration;
                        // Only publish another generation after the current generation is acknowledged.
                        int ack = ImplicitImage.ReadInt(incoming, 16);
                        if (ack == ImplicitImage.ReadInt(frame, 12))
                        { frame = image.BuildAppToPlc(); Check(Native.SetImage(frame, frame.Length)); }
                    }
                    if (image.CheckStale(DateTimeOffset.UtcNow, TimeSpan.FromMilliseconds(Math.Max(1000L, (long)io.RpiMs * 10))) && received)
                        throw new IOException("PLC data became stale. Adapter stopped; restart the host to reconnect.");
                } while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false));
            }
            finally { Check(Native.Stop()); }
        }
        finally { image.CheckStale(DateTimeOffset.MaxValue, TimeSpan.FromTicks(1)); lifetime.Release(); }
    }
    public async ValueTask DisposeAsync()
    {
        // Caller cancels and awaits RunAsync first. Never unload a library with an active worker.
        await lifetime.WaitAsync().ConfigureAwait(false);
        try { disposed = true; } finally { lifetime.Release(); }
    }
    private static void Check(int status) { if (status != 0) throw new IOException($"OpENer bridge error {status}."); }
    private static class Native
    {
        private const string Library = "LogixTagRelayOpener";
        [DllImport(Library, EntryPoint = "ltr_abi_version", CallingConvention = CallingConvention.Cdecl)] internal static extern int AbiVersion();
        [DllImport(Library, EntryPoint = "ltr_start", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
        internal static extern int Start(string nic, int inputBytes, int outputBytes, int inputAssembly, int outputAssembly, int configAssembly, uint serial, ushort seed);
        [DllImport(Library, EntryPoint = "ltr_stop", CallingConvention = CallingConvention.Cdecl)] internal static extern int Stop();
        [DllImport(Library, EntryPoint = "ltr_set_image", CallingConvention = CallingConvention.Cdecl)] internal static extern int SetImage(byte[] image, int size);
        [DllImport(Library, EntryPoint = "ltr_get_image", CallingConvention = CallingConvention.Cdecl)] internal static extern int GetImage([Out] byte[] image, int size, out int generation);
    }
}
