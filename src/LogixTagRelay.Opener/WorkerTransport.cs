using System.Diagnostics;
using System.Reflection;
using System.Text.Json;

namespace LogixTagRelay.Opener;

internal sealed record WorkerRequest(string? Configuration = null, string? Nic = null, uint Serial = 1, byte[]? Frame = null, bool Probe = false);
internal sealed record WorkerReply(int Status, byte[]? Frame = null, string? Error = null, bool Permanent = false);

/// <summary>Private, exact-owned child process. Native stack lifetime never shares the consumer process.</summary>
internal sealed class WorkerTransport : IImplicitTransport
{
    private readonly Process process;
    private readonly string prefix;
    private readonly Task stderrDrain;
    private readonly SemaphoreSlim gate = new(1, 1);
    private bool disposed;
    private WorkerTransport(Process process, string token)
    {
        this.process = process; prefix = "LTR-" + token + ":";
        _ = process.SafeHandle; // Retain the creation handle throughout shutdown; never rediscover by PID/name.
        process.StandardInput.AutoFlush = true;
        stderrDrain = DrainAsync(process.StandardError);
    }
    private static async Task DrainAsync(StreamReader reader) { while (await reader.ReadLineAsync().ConfigureAwait(false) is not null) { } }
    private static WorkerTransport Start()
    {
        string entry = Assembly.GetEntryAssembly()?.Location ?? "";
        string runtime = Path.ChangeExtension(entry, ".runtimeconfig.json"), deps = Path.ChangeExtension(entry, ".deps.json");
        if (!File.Exists(runtime) || !File.Exists(deps)) throw new NotSupportedException("OpENer worker hosting requires a framework-dependent app with runtimeconfig/deps files; single-file deployment is not supported.");
        string token = Guid.NewGuid().ToString("N");
        var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string arg in new[] { "exec", "--runtimeconfig", runtime, "--depsfile", deps, typeof(OpenerAdapter).Assembly.Location, "--relay-worker", token }) start.ArgumentList.Add(arg);
        return new(Process.Start(start) ?? throw new IOException("Could not start the OpENer worker."), token);
    }
    public static async ValueTask<IImplicitTransport> ConnectAsync(SyncConfiguration config, string nic, uint serial, CancellationToken ct)
    {
        if (!OperatingSystem.IsWindows() || !Environment.Is64BitProcess) throw new PlatformNotSupportedException("Windows x64 is required.");
        var worker = Start();
        try { await worker.RequestAsync(new(config.ToJson(), nic, serial), ct).ConfigureAwait(false); return worker; }
        catch { await worker.DisposeAsync().ConfigureAwait(false); throw; }
    }
    public static async Task<int> ProbeAsync(CancellationToken ct)
    {
        await using var worker = Start();
        return (await worker.RequestAsync(new(Probe: true), ct).ConfigureAwait(false)).Status;
    }
    private async Task<WorkerReply> RequestAsync(WorkerRequest request, CancellationToken ct)
    {
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(15));
            await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(request).AsMemory(), timeout.Token).ConfigureAwait(false);
            while (await process.StandardOutput.ReadLineAsync(timeout.Token).ConfigureAwait(false) is { } line)
            {
                int at = line.IndexOf(prefix, StringComparison.Ordinal); if (at < 0) continue;
                var reply = JsonSerializer.Deserialize<WorkerReply>(line[(at + prefix.Length)..]) ?? throw new IOException("Empty worker reply.");
                if (reply.Error is not null) { if (reply.Permanent) throw new NotSupportedException(reply.Error); throw new IOException(reply.Error); }
                return reply;
            }
            throw new IOException("OpENer worker exited before replying.");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new TimeoutException("OpENer worker did not respond."); }
        finally { gate.Release(); }
    }
    public async ValueTask<byte[]?> ExchangeAsync(byte[] image, CancellationToken cancellationToken) =>
        (await RequestAsync(new(Frame: image), cancellationToken).ConfigureAwait(false)).Frame;
    public async ValueTask DisposeAsync()
    {
        if (disposed) return; disposed = true;
        try
        {
            try { process.StandardInput.Close(); } catch (IOException) { }
            try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(6)).ConfigureAwait(false); }
            catch (TimeoutException)
            {
                // Only this directly created process, identified by its retained creation handle.
                process.Kill(entireProcessTree: false); await process.WaitForExitAsync().ConfigureAwait(false);
            }
            await stderrDrain.ConfigureAwait(false);
        }
        finally { process.Dispose(); }
    }
}
