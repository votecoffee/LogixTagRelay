using System.Runtime.InteropServices;
using System.Text.Json;

namespace LogixTagRelay.Opener;

internal static class NativeBridge
{
    private const string Library = "LogixTagRelayOpener";
    [DllImport(Library, EntryPoint = "ltr_abi_version", CallingConvention = CallingConvention.Cdecl)] internal static extern int AbiVersion();
    [DllImport(Library, EntryPoint = "ltr_connection_epoch", CallingConvention = CallingConvention.Cdecl)] internal static extern int Epoch();
    [DllImport(Library, EntryPoint = "ltr_set_image_at_epoch", CallingConvention = CallingConvention.Cdecl)] internal static extern int SetAtEpoch(byte[] image, int size, int epoch);
    [DllImport(Library, EntryPoint = "ltr_start", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    internal static extern int Start(string nic, int inputBytes, int outputBytes, int inputAssembly, int outputAssembly, int configAssembly, uint serial, ushort seed);
    [DllImport(Library, EntryPoint = "ltr_stop", CallingConvention = CallingConvention.Cdecl)] internal static extern int Stop();
    [DllImport(Library, EntryPoint = "ltr_set_image", CallingConvention = CallingConvention.Cdecl)] internal static extern int SetImage(byte[] image, int size);
    [DllImport(Library, EntryPoint = "ltr_get_image", CallingConvention = CallingConvention.Cdecl)] internal static extern int GetImage([Out] byte[] image, int size, out int generation);
}

internal static class WorkerEntryPoint
{
    public static async Task<int> Main(string[] args)
    {
        if (args is not ["--relay-worker", var token] || token.Length != 32) return 2;
        string prefix = "LTR-" + token + ":"; bool started = false;
        void Reply(WorkerReply result) => Console.WriteLine(prefix + JsonSerializer.Serialize(result));
        try
        {
            var first = JsonSerializer.Deserialize<WorkerRequest>(await Console.In.ReadLineAsync() ?? "") ?? throw new FormatException("Missing worker initialization.");
            if (first.Probe) { _ = NativeBridge.Epoch(); Reply(new(NativeBridge.AbiVersion())); return 0; }
            var config = SyncConfiguration.FromJson(first.Configuration ?? throw new FormatException("Missing contract."));
            var io = config.Implicit ?? throw new FormatException("Missing implicit options.");
            if (NativeBridge.AbiVersion() != 2) throw new NotSupportedException("OpENer native ABI 2 is required.");
            int status = NativeBridge.Start(first.Nic ?? "", config.AppToPlcBytes, config.PlcToAppBytes,
                io.PlcInputAssembly, io.PlcOutputAssembly, io.ConfigurationAssembly, first.Serial,
                (ushort)System.Security.Cryptography.RandomNumberGenerator.GetInt32(1, ushort.MaxValue));
            if (status != 0) throw new IOException($"OpENer worker start failed ({status}); check the selected NIC and EtherNet/IP ports.");
            started = true; Reply(new(0));
            var incoming = new byte[config.PlcToAppBytes]; int epoch = NativeBridge.Epoch(); bool seenConnection = false;
            while (await Console.In.ReadLineAsync() is { } line)
            {
                var request = JsonSerializer.Deserialize<WorkerRequest>(line) ?? throw new FormatException("Empty worker request.");
                if (request.Frame is not { } image || image.Length != config.AppToPlcBytes) throw new FormatException("Worker image size mismatch.");
                int currentEpoch = NativeBridge.Epoch();
                if (seenConnection && currentEpoch != epoch) throw new IOException("PLC connection/mode epoch changed; obtain a fresh baseline.");
                epoch = currentEpoch;
                if (NativeBridge.SetAtEpoch(image, image.Length, epoch) != 0) throw new IOException("Native connection changed before image publication.");
                status = NativeBridge.GetImage(incoming, incoming.Length, out _);
                if (status < 0) throw new IOException("Native network worker failed.");
                if (seenConnection && NativeBridge.Epoch() != epoch) throw new IOException("PLC connection changed while reading its image.");
                if (status == 1) seenConnection = true;
                Reply(new(status, status == 1 ? incoming : null));
            }
            return 0;
        }
        catch (Exception error) { Reply(new(-1, Error: error.Message, Permanent: error is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException or NotSupportedException or FormatException)); return 1; }
        finally { if (started) NativeBridge.Stop(); }
    }
}
