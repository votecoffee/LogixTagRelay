using System.Buffers.Binary;

namespace LogixTagRelay.Emulation;

/// <summary>Emulated PLC-side image protocol counters and current command-apply state.</summary>
/// <param name="Result">Most recent protocol outcome.</param>
/// <param name="Exchanges">Number of API image exchanges processed.</param>
/// <param name="Accepted">Number of new commands accepted.</param>
/// <param name="Sequence">Current positive PLC image sequence.</param>
/// <param name="Acknowledgement">Most recently accepted API command sequence.</param>
/// <param name="ConflictBytes">Number of payload bytes for which concurrent PLC changes won.</param>
/// <param name="ApplyCommands">Whether emulated API payload application is enabled.</param>
public sealed record ImplicitPlcStatus(string Result, long Exchanges, long Accepted, int Sequence, int Acknowledgement,
    int ConflictBytes, bool ApplyCommands);

/// <summary>Contract v3 PLC-side image exchange. Does not execute PLC logic or implement an EtherNet/IP scanner.</summary>
public sealed class ImplicitPlc
{
    private readonly PlcMemory memory;
    private readonly SyncConfiguration config;
    private readonly int schema;
    private byte[]? published;
    private int sequence, acknowledgement, conflicts, resyncNonce;
    private bool resyncPending;
    private long exchanges, accepted;
    private bool applyCommands = true;
    private string result = "Waiting";
    /// <summary>Creates an emulated PLC image protocol over shared memory; does not execute PLC logic.</summary>
    /// <param name="memory">Shared emulated PLC memory.</param>
    public ImplicitPlc(PlcMemory memory)
    {
        this.memory = memory; config = memory.Configuration;
        if (config.Implicit is null) throw new ArgumentException("Contract has no implicit settings.");
        schema = BinaryPrimitives.ReadInt32LittleEndian(Convert.FromHexString(config.SchemaHash));
    }
    /// <summary>Whether accepted API image payloads may modify emulated PLC memory.</summary>
    public bool ApplyCommands { get { lock (memory.Gate) return applyCommands; } set { lock (memory.Gate) applyCommands = value; } }
    /// <summary>Consistent snapshot of current emulator protocol or script diagnostics.</summary>
    public ImplicitPlcStatus Status { get { lock (memory.Gate) return new(result, exchanges, accepted, sequence, acknowledgement, conflicts, applyCommands); } }
    /// <summary>Returns a copy of the currently published PLC-to-API image.</summary>
    public byte[] Output() { lock (memory.Gate) { if (published is null) Publish(); return published!.ToArray(); } }
    /// <summary>Processes an API image using the generated wrapper&apos;s baseline, acknowledgement and PLC-first merge model.</summary>
    /// <param name="frame">Full symmetric relay image to exchange with the emulator.</param>
    public byte[] Exchange(ReadOnlySpan<byte> frame)
    {
        lock (memory.Gate)
        {
            exchanges++; conflicts = 0;
            if (published is null) Publish();
            if (frame.Length == config.AppToPlcBytes && ImplicitImage.ReadInt(frame, 0) == ImplicitImage.Magic && ImplicitImage.ReadInt(frame, 4) == ImplicitImage.ProtocolVersion && ImplicitImage.ReadInt(frame, 8) == schema && ImplicitImage.ReadInt(frame, 20) == 2 && ImplicitImage.ReadInt(frame, 16) > 0)
            {
                int nonce = ImplicitImage.ReadInt(frame, 16);
                if (nonce != resyncNonce) { resyncNonce = nonce; resyncPending = true; sequence = 0; acknowledgement = 0; Publish(); }
                result = "Resynchronization baseline"; return published!.ToArray();
            }
            string? reject = null;
            if (frame.Length != config.AppToPlcBytes) reject = "Size mismatch";
            else if (ImplicitImage.ReadInt(frame, 20) != 1) reject = "Bootstrap / invalid frame";
            else if (ImplicitImage.ReadInt(frame, 0) != ImplicitImage.Magic || ImplicitImage.ReadInt(frame, 4) != ImplicitImage.ProtocolVersion || ImplicitImage.ReadInt(frame, 8) != schema) reject = "Header mismatch";
            else if (!applyCommands) reject = "Commands disabled";
            else if (ImplicitImage.ReadInt(frame, 12) <= 0) reject = "Invalid command sequence";
            else if (ImplicitImage.ReadInt(frame, 12) == acknowledgement) reject = "Duplicate";
            else if (ImplicitImage.ReadInt(frame, 16) != sequence) reject = "Baseline mismatch";
            if (reject is not null) { result = reject; return published!.ToArray(); }

            var received = frame.ToArray();
            try
            {
                // Match the generated wrapper: validate every incoming STRING before any merge.
                foreach (var field in config.Fields.Where(f => f.Type == PlcType.STRING)) _ = PlcTypes.Decode(field, received.AsSpan(field.Offset));
                var current = Pack();
                foreach (var field in config.Fields.Where(f => f.Direction == Direction.PlcToApp))
                    current.AsSpan(field.Offset, (field.WireBytes + 3) / 4 * 4).CopyTo(received.AsSpan(field.Offset));
                for (int i = ImplicitImage.HeaderBytes; i < current.Length; i++)
                {
                    if (current[i] == published![i]) current[i] = received[i];
                    else if (received[i] != published![i] && received[i] != current[i]) conflicts++;
                }
                // A byte-level merge can make STRING LEN invalid. Stage all values before committing.
                var writes = config.Fields.Where(f => f.CanWrite).Select(f => new MemoryWrite(f.Path,
                    PlcTypes.Decode(f, current.AsSpan(f.Offset)), PlcTypes.Decode(f, received.AsSpan(f.Offset)))).ToArray();
                memory.Apply(writes, "API implicit", fromApi: true);
            }
            catch (FormatException) { result = "Invalid STRING length"; return published!.ToArray(); }
            acknowledgement = ImplicitImage.ReadInt(frame, 12); accepted++; resyncPending = false; result = "Accepted";
            Publish();
            return published!.ToArray();
        }
    }
    private byte[] Pack()
    {
        var image = new byte[config.PlcToAppBytes];
        foreach (var field in config.Fields) PlcTypes.Encode(field, memory.Read(field.Path), image.AsSpan(field.Offset));
        return image;
    }
    private void Publish()
    {
        var next = Pack();
        sequence = sequence == int.MaxValue ? 1 : sequence + 1;
        int[] header = [ImplicitImage.Magic, ImplicitImage.ProtocolVersion, schema, sequence, resyncPending ? resyncNonce : acknowledgement, resyncPending ? 3 : 1];
        for (int i = 0; i < header.Length; i++) ImplicitImage.WriteInt(next, i * 4, header[i]);
        published = next;
    }
}
