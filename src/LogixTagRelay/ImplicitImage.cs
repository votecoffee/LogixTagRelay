using System.Buffers.Binary;

namespace LogixTagRelay;

/// <summary>
/// Pure assembly codec. It does NOT implement an EtherNet/IP adapter or send UDP.
/// Native adapters must supply complete immutable assembly payloads, excluding CIP headers.
/// </summary>
public sealed class ImplicitImage
{
    public const int HeaderBytes = 24;
    public const int Magic = 0x50535031;
    private readonly SyncConfiguration config;
    private readonly IPlcModel model;
    private readonly int schemaToken;
    private readonly object gate = new();
    private readonly Queue<SentImage> pending = new();
    private int sequence = System.Security.Cryptography.RandomNumberGenerator.GetInt32(1, int.MaxValue - 32);
    private int? lastPlcSequence;
    private DateTimeOffset? lastFresh;
    private sealed record SentImage(int Sequence, List<(IPlcVariable Variable, long Version)> Versions);

    public ImplicitImage(SyncConfiguration config, IPlcModel model)
    {
        ModelValidation.Validate(config, model);
        this.config = config; this.model = model;
        schemaToken = BinaryPrimitives.ReadInt32LittleEndian(Convert.FromHexString(config.SchemaHash).AsSpan(0, 4));
    }
    public byte[] BuildAppToPlc()
    {
        lock (gate)
        {
            // At most 32 outstanding generations; resending a captured frame is the adapter's responsibility.
            if (pending.Count >= 32) throw new InvalidOperationException("PLC acknowledgement has stalled.");
            if (model.Variables.Any(v => v.Map.Direction == Direction.AppToPlc && v.Version == 0))
                throw new InvalidOperationException("Assign every app-owned value before publishing an implicit image.");
            var image = new byte[config.AppToPlcBytes];
            int next = sequence = (sequence + 1) & int.MaxValue;
            WriteHeader(image, next, lastPlcSequence ?? 0);
            var versions = new List<(IPlcVariable, long)>();
            foreach (var variable in model.Variables.Where(v => v.Map.Direction == Direction.AppToPlc))
            {
                var captured = variable.Capture();
                PlcTypes.Encode(variable.Map.Type, captured.Value, image.AsSpan(variable.Map.Offset));
                versions.Add((variable, captured.Version));
            }
            pending.Enqueue(new(next, versions));
            return image;
        }
    }
    public void ReceivePlcToApp(ReadOnlySpan<byte> image, DateTimeOffset time)
    {
        lock (gate)
        {
            try
            {
                if (image.Length != config.PlcToAppBytes || ReadInt(image, 0) != Magic ||
                    ReadInt(image, 4) != 1 || ReadInt(image, 8) != schemaToken || ReadInt(image, 20) != 1)
                    throw new FormatException("Assembly size, version, schema, or valid flag mismatch.");
                int plcSequence = ReadInt(image, 12);
                if (plcSequence < 0) throw new FormatException("Expected a nonnegative 31-bit sequence.");
                if (lastPlcSequence.HasValue)
                {
                    int delta = unchecked(plcSequence - lastPlcSequence.Value) & int.MaxValue;
                    if (delta == 0 || delta > int.MaxValue / 2) return;
                }
                var decoded = model.Variables.Where(v => v.Map.Direction == Direction.PlcToApp)
                    .Select(v => v).ToArray();
                foreach (var variable in decoded)
                    variable.AcceptRead(PlcTypes.Decode(variable.Map.Type, image[variable.Map.Offset..]), time);
                lastPlcSequence = plcSequence;
                lastFresh = time;
                int ack = ReadInt(image, 16);
                // A transport send is not PLC acceptance. Only an exact echoed generation confirms it.
                if (pending.Any(p => p.Sequence == ack))
                {
                    SentImage sent;
                    do
                    {
                        sent = pending.Dequeue();
                        foreach (var (variable, version) in sent.Versions) variable.AcceptWrite(version, time);
                    } while (sent.Sequence != ack);
                }
            }
            catch { foreach (var variable in model.Variables) variable.MarkBad(); throw; }
        }
    }
    public bool CheckStale(DateTimeOffset now, TimeSpan timeout)
    {
        if (timeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(timeout));
        lock (gate)
        {
            bool stale = lastFresh is null || now - lastFresh > timeout;
            if (stale) foreach (var variable in model.Variables) variable.MarkBad();
            return stale;
        }
    }
    private void WriteHeader(Span<byte> image, int seq, int ack)
    {
        WriteInt(image, 0, Magic); WriteInt(image, 4, 1); WriteInt(image, 8, schemaToken);
        WriteInt(image, 12, seq); WriteInt(image, 16, ack); WriteInt(image, 20, 1);
    }
    public static int ReadInt(ReadOnlySpan<byte> image, int offset) => BinaryPrimitives.ReadInt32LittleEndian(image[offset..]);
    public static void WriteInt(Span<byte> image, int offset, int value) => BinaryPrimitives.WriteInt32LittleEndian(image[offset..], value);
}
