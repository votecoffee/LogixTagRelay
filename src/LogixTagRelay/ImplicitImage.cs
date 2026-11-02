using System.Buffers.Binary;

namespace LogixTagRelay;

/// <summary>Complete assembly codec; no network activity. One request per matching PLC snapshot.</summary>
public sealed class ImplicitImage
{
    public const int HeaderBytes = 24;
    public const int Magic = 0x50535031;
    public const int ProtocolVersion = 3;
    private readonly SyncConfiguration config;
    private readonly IPlcModel model;
    private readonly int schemaToken;
    private readonly object gate = new();
    private SentImage? pending;
    private int sequence = System.Security.Cryptography.RandomNumberGenerator.GetInt32(1, int.MaxValue - 32);
    private int? lastPlcSequence;
    private DateTimeOffset? lastFresh;
    private byte[]? lastReceived;
    private sealed record SentImage(int Sequence, List<(IPlcVariable Variable, long Version)> Versions);
    public bool HasPendingCommand { get { lock (gate) return pending is not null; } }

    public ImplicitImage(SyncConfiguration config, IPlcModel model)
    {
        ModelValidation.Validate(config, model);
        this.config = config; this.model = model;
        schemaToken = BinaryPrimitives.ReadInt32LittleEndian(Convert.FromHexString(config.SchemaHash).AsSpan(0, 4));
    }
    /// <summary>Invalid startup frame: cannot authorize PLC writes before a baseline is received.</summary>
    public byte[] BuildBootstrap() => new byte[config.AppToPlcBytes];
    public byte[] BuildAppToPlc()
    {
        lock (gate)
        {
            if (pending is not null) throw new InvalidOperationException("Resend the captured frame until its PLC acknowledgement arrives.");
            if (lastReceived is null) throw new InvalidOperationException("Receive a valid PLC snapshot before publishing commands.");
            var image = lastReceived.ToArray();
            int next = sequence = sequence == int.MaxValue ? 1 : sequence + 1;
            WriteHeader(image, next, lastPlcSequence!.Value);
            var versions = new List<(IPlcVariable, long)>();
            foreach (var variable in model.Variables.Where(v => v.Map.CanWrite))
            {
                var captured = variable.CaptureForMerge();
                // Unedited values echo the PLC. No CLR default can overwrite startup values.
                if (captured.Dirty) PlcTypes.Encode(variable.Map, captured.Value, image.AsSpan(variable.Map.Offset));
                versions.Add((variable, captured.Version));
            }
            pending = new(next, versions);
            return image;
        }
    }
    public bool ReceivePlcToApp(ReadOnlySpan<byte> image, DateTimeOffset time)
    {
        lock (gate)
        {
            try
            {
                if (image.Length != config.PlcToAppBytes || ReadInt(image, 0) != Magic ||
                    ReadInt(image, 4) != ProtocolVersion || ReadInt(image, 8) != schemaToken || ReadInt(image, 20) != 1)
                    throw new FormatException("Assembly size, version, schema, or valid flag mismatch.");
                int plcSequence = ReadInt(image, 12);
                if (plcSequence <= 0) throw new FormatException("Expected a positive 31-bit PLC sequence.");
                if (lastPlcSequence.HasValue)
                {
                    int delta = unchecked(plcSequence - lastPlcSequence.Value) & int.MaxValue;
                    if (delta == 0 || delta > int.MaxValue / 2) return false;
                    if (pending is null || ReadInt(image, 16) != pending.Sequence)
                        throw new FormatException("PLC snapshot advanced without acknowledging the pending command. Restart the session after reconciling PLC state.");
                }
                var decoded = new List<(IPlcVariable Variable, object Value)>();
                foreach (var variable in model.Variables)
                    decoded.Add((variable, PlcTypes.Decode(variable.Map, image[variable.Map.Offset..])));
                // Decode the entire frame before publishing any values or accepting a generation.
                foreach (var (variable, value) in decoded)
                {
                    var sent = pending?.Versions.SingleOrDefault(p => ReferenceEquals(p.Variable, variable));
                    if (sent is { Variable: not null } written) variable.AcceptMergedWrite(written.Version, value, time);
                    else variable.Observe(value, time);
                }
                lastReceived = image.ToArray(); lastPlcSequence = plcSequence; lastFresh = time; pending = null;
                return true;
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
        WriteInt(image, 0, Magic); WriteInt(image, 4, ProtocolVersion); WriteInt(image, 8, schemaToken);
        WriteInt(image, 12, seq); WriteInt(image, 16, ack); WriteInt(image, 20, 1);
    }
    public static int ReadInt(ReadOnlySpan<byte> image, int offset) => BinaryPrimitives.ReadInt32LittleEndian(image[offset..]);
    public static void WriteInt(Span<byte> image, int offset, int value) => BinaryPrimitives.WriteInt32LittleEndian(image[offset..], value);
}
