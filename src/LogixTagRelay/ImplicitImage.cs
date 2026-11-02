using System.Buffers.Binary;

namespace LogixTagRelay;

/// <summary>Complete assembly codec; no network activity. One request per matching PLC snapshot.</summary>
public sealed class ImplicitImage
{
    /// <summary>Relay header size in bytes, included in the 480-byte maximum image size.</summary>
    public const int HeaderBytes = 24;
    /// <summary>Relay wire signature used to reject unrelated images.</summary>
    public const int Magic = 0x50535031;
    /// <summary>Relay image protocol version; both PLC and API peers must match.</summary>
    public const int ProtocolVersion = 3;
    private readonly SyncConfiguration config;
    private readonly IPlcModel model;
    private readonly int schemaToken;
    private readonly object gate = new();
    private SentImage? pending;
    private int? resynchronizationNonce;
    private bool awaitingResynchronization;
    private int sequence = System.Security.Cryptography.RandomNumberGenerator.GetInt32(1, int.MaxValue - 32);
    private int? lastPlcSequence;
    private DateTimeOffset? lastFresh;
    private byte[]? lastReceived;
    private sealed record SentImage(int Sequence, List<(IPlcVariable Variable, long Version)> Versions);
    /// <summary>Whether a captured command is waiting for its matching PLC acknowledgement.</summary>
    public bool HasPendingCommand { get { lock (gate) return pending is not null; } }

    /// <summary>Creates image protocol state for a matching generated model and symmetric configuration.</summary>
    /// <param name="config">Matching generated contract configuration.</param>
    /// <param name="model">Live application model instance used by synchronization.</param>
    public ImplicitImage(SyncConfiguration config, IPlcModel model)
    {
        ModelValidation.Validate(config, model);
        this.config = config; this.model = model;
        schemaToken = BinaryPrimitives.ReadInt32LittleEndian(Convert.FromHexString(config.SchemaHash).AsSpan(0, 4));
    }
    /// <summary>Invalid startup frame: cannot authorize PLC writes before a baseline is received.</summary>
    public byte[] BuildBootstrap() => new byte[config.AppToPlcBytes];
    /// <summary>Starts a fresh-nonce baseline handshake and discards old pending protocol state without clearing model edits.</summary>
    public byte[] BeginResynchronization()
    {
        lock (gate)
        {
            resynchronizationNonce = System.Security.Cryptography.RandomNumberGenerator.GetInt32(1, int.MaxValue);
            awaitingResynchronization = true; pending = null; lastReceived = null; lastPlcSequence = null; lastFresh = null;
            var request = new byte[config.AppToPlcBytes]; WriteHeader(request, 0, resynchronizationNonce.Value); WriteInt(request, 20, 2);
            return request;
        }
    }
    /// <summary>Captures a command using a valid PLC baseline. Retain and resend the same bytes until acknowledged.</summary>
    /// <param name="allowWrites">Whether pending application edits may enter the captured command image.</param>
    public byte[] BuildAppToPlc(bool allowWrites = true)
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
                if (!allowWrites) continue;
                if (captured.Dirty) PlcTypes.Encode(variable.Map, captured.Value, image.AsSpan(variable.Map.Offset));
                versions.Add((variable, captured.Version));
            }
            pending = new(next, versions);
            return image;
        }
    }
    /// <summary>Validates and adopts a fresh PLC image; returns false for ignored stale, duplicate or unrelated handshake images.</summary>
    /// <param name="image">Complete symmetric relay image including its header.</param>
    /// <param name="time">Timestamp assigned to the accepted observation or acknowledgement.</param>
    public bool ReceivePlcToApp(ReadOnlySpan<byte> image, DateTimeOffset time)
    {
        lock (gate)
        {
            try
            {
                if (image.Length != config.PlcToAppBytes || ReadInt(image, 0) != Magic ||
                    ReadInt(image, 4) != ProtocolVersion || ReadInt(image, 8) != schemaToken || ReadInt(image, 20) is not (1 or 3))
                    throw new FormatException("Assembly size, version, schema, or valid flag mismatch.");
                if (awaitingResynchronization && (ReadInt(image, 20) != 3 || ReadInt(image, 16) != resynchronizationNonce)) return false;
                if (!awaitingResynchronization && ReadInt(image, 20) == 3 && ReadInt(image, 16) != resynchronizationNonce) return false;
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
                awaitingResynchronization = false;
                lastReceived = image.ToArray(); lastPlcSequence = plcSequence; lastFresh = time; pending = null;
                return true;
            }
            catch { foreach (var variable in model.Variables) variable.MarkBad(); throw; }
        }
    }
    /// <summary>Marks variables bad and throws TimeoutException when the accepted PLC image is too old.</summary>
    /// <param name="now">Current scheduling timestamp.</param>
    /// <param name="timeout">Maximum acceptable age of the last fresh PLC image.</param>
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
    /// <summary>Reads one little-endian DINT from the image at a byte offset.</summary>
    /// <param name="image">Complete symmetric relay image including its header.</param>
    /// <param name="offset">Byte offset of a little-endian DINT in the image.</param>
    public static int ReadInt(ReadOnlySpan<byte> image, int offset) => BinaryPrimitives.ReadInt32LittleEndian(image[offset..]);
    /// <summary>Writes one little-endian DINT to the image at a byte offset.</summary>
    /// <param name="image">Complete symmetric relay image including its header.</param>
    /// <param name="offset">Byte offset of a little-endian DINT in the image.</param>
    /// <param name="value">Typed scalar or timestamp value required by this operation.</param>
    public static void WriteInt(Span<byte> image, int offset, int value) => BinaryPrimitives.WriteInt32LittleEndian(image[offset..], value);
}
