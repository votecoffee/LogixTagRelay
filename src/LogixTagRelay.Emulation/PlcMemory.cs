using System.Security.Cryptography;
using System.Text;

namespace LogixTagRelay.Emulation;

/// <summary>One lossless text-valued PLC memory entry with version and last-writer metadata.</summary>
/// <param name="Map">Scalar mapping for this memory entry.</param>
/// <param name="Role">Data or control-tag role shown by the emulator.</param>
/// <param name="Value">Lossless scalar text or typed value associated with this request.</param>
/// <param name="Version">Value change version for this memory entry.</param>
/// <param name="LastWriter">Source of the last value change.</param>
/// <param name="ChangedAt">Timestamp of the last value change, when any.</param>
/// <param name="LastApiValue">Lossless text of the most recent API write, when any.</param>
/// <param name="LastApiWriteAt">Timestamp of the most recent API write, even when its value did not change.</param>
public sealed record MemoryTag(FieldMap Map, string Role, string Value, long Version, string LastWriter,
    DateTimeOffset? ChangedAt, string? LastApiValue, DateTimeOffset? LastApiWriteAt);
/// <summary>Consistent revision and ordered memory entries for GUI and script reads.</summary>
/// <param name="Revision">Atomic memory value revision used for stale script transaction rejection.</param>
/// <param name="Tags">Ordered catalog or memory tag entries.</param>
public sealed record MemorySnapshot(long Revision, IReadOnlyList<MemoryTag> Tags);
/// <summary>A staged typed memory write with optional original API command for diagnostics.</summary>
/// <param name="Path">Fully qualified Logix path including optional program scope and array coordinates.</param>
/// <param name="Value">Lossless scalar text or typed value associated with this request.</param>
/// <param name="RequestedApiValue">Original typed API command retained for diagnostics, when different from the merged value.</param>
public sealed record MemoryWrite(string Path, object Value, object? RequestedApiValue = null);

/// <summary>Typed PLC-side memory only. Contains no PLC program or network driver.</summary>
public sealed class PlcMemory
{
    internal object Gate { get; } = new();
    private readonly SyncConfiguration config;
    private readonly Dictionary<string, Entry> entries = new(StringComparer.OrdinalIgnoreCase);
    private long revision;
    private sealed class Entry(FieldMap map, string role, bool apiWritable)
    {
        public FieldMap Map { get; } = map;
        public string Role { get; } = role;
        public bool ApiWritable { get; } = apiWritable;
        public object Value { get; set; } = TagText.Default(map.Type);
        public long Version { get; set; }
        public string LastWriter { get; set; } = "Initial";
        public DateTimeOffset? ChangedAt { get; set; }
        public string? LastApiValue { get; set; }
        public DateTimeOffset? LastApiWriteAt { get; set; }
    }
    /// <summary>Creates typed shared memory and independent control tags from a validated contract configuration.</summary>
    /// <param name="configuration">Matching generated contract configuration.</param>
    public PlcMemory(SyncConfiguration configuration)
    {
        config = SyncConfiguration.FromJson(configuration.ToJson());
        foreach (var field in config.Fields) entries.Add(field.Path, new(field, field.Path.Equals(config.Heartbeat?.Tag, StringComparison.OrdinalIgnoreCase) ? "Heartbeat" : "Data", field.CanWrite));
        if (config.Heartbeat is { } heartbeat && !entries.ContainsKey(heartbeat.Tag)) entries.Add(heartbeat.Tag, new(new(heartbeat.Tag, PlcType.BOOL, Direction.AppToPlc, 24), "Heartbeat", true));
        var options = config.Explicit;
        void Control(string? path, string role, bool writable)
        {
            if (path is not null) entries.Add(path, new(new(path, PlcType.DINT, writable ? Direction.AppToPlc : Direction.PlcToApp, 24), role, writable));
        }
        Control(options?.TriggerTag, "Trigger", false); Control(options?.MirrorTag, "Handled", true); Control(options?.ResponseTag, "Response", true);
    }
    // Return a copy so callers cannot mutate the mapping held by a running emulator.
    /// <summary>A defensive copy of the session&apos;s contract configuration.</summary>
    public SyncConfiguration Configuration => SyncConfiguration.FromJson(config.ToJson());
    /// <summary>Contract identity including layout and control settings, used to reject stale emulator clients.</summary>
    public string Fingerprint => ContractFingerprint(config);
    /// <summary>Computes deterministic emulator contract identity from layout, endpoint-independent controls and communication settings.</summary>
    /// <param name="config">Matching generated contract configuration.</param>
    public static string ContractFingerprint(SyncConfiguration config)
    {
        config.Validate();
        string identity = string.Join("|", config.SchemaVersion, config.SchemaHash, config.PlcToAppBytes, config.AppToPlcBytes,
            config.Explicit?.TriggerTag?.ToUpperInvariant(), config.Explicit?.MirrorTag?.ToUpperInvariant(), config.Explicit?.ResponseTag?.ToUpperInvariant(),
            config.Implicit is not null, config.Heartbeat?.Tag.ToUpperInvariant(), config.Heartbeat?.IntervalMs);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
    }
    private Entry Find(string path) => entries.TryGetValue(path, out var entry) ? entry : throw new ArgumentException($"Unknown contract tag: {path}");
    /// <summary>Immutable scalar mapping and its PLC access permissions.</summary>
    /// <param name="path">Fully qualified Logix scalar path.</param>
    public FieldMap Map(string path) { lock (Gate) return Find(path).Map; }
    /// <summary>Reads one scalar from shared emulated memory.</summary>
    /// <param name="path">Fully qualified Logix scalar path.</param>
    public object Read(string path) { lock (Gate) return Find(path).Value; }
    /// <summary>Parses lossless scalar text and commits a PLC-side memory edit.</summary>
    /// <param name="path">Fully qualified Logix scalar path.</param>
    /// <param name="value">Typed scalar or timestamp value required by this operation.</param>
    /// <param name="writer">Diagnostic source label for this memory edit.</param>
    public void SetText(string path, string value, string writer = "GUI") => Set(path, TagText.Parse(Map(path), value), writer);
    /// <summary>Commits a typed PLC-side memory edit after validating its representation.</summary>
    /// <param name="path">Fully qualified Logix scalar path.</param>
    /// <param name="value">Typed scalar or timestamp value required by this operation.</param>
    /// <param name="writer">Diagnostic source label for this memory edit.</param>
    public void Set(string path, object value, string writer = "PLC") => Apply([new(path, value)], writer);
    /// <summary>Current immutable diagnostics with data age evaluated at the time of access.</summary>
    public MemorySnapshot Snapshot()
    {
        lock (Gate) return new(revision, entries.Values.Select(e => new MemoryTag(e.Map, e.Role, TagText.Format(e.Value), e.Version,
            e.LastWriter, e.ChangedAt, e.LastApiValue, e.LastApiWriteAt)).ToArray());
    }
    /// <summary>Validate all writes before committing. Optional revision rejects a stale script transaction.</summary>
    /// <param name="writer">Diagnostic source label for this memory edit.</param>
    /// <param name="writes">Typed scalar edits to validate and commit together.</param>
    /// <param name="expectedRevision">Optional expected memory revision; a stale transaction returns false without applying any edits.</param>
    /// <param name="fromApi">Whether to enforce API write ownership and record the original command diagnostics.</param>
    public bool Apply(IReadOnlyList<MemoryWrite> writes, string writer, long? expectedRevision = null, bool fromApi = false)
    {
        lock (Gate)
        {
            if (expectedRevision.HasValue && expectedRevision != revision) return false;
            var staged = writes.Select(w => (Entry: Find(w.Path), Write: w)).ToArray();
            if (staged.Select(x => x.Entry.Map.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count() != staged.Length)
                throw new ArgumentException("A transaction may write each tag once.");
            foreach (var (entry, write) in staged)
            {
                if (fromApi && !entry.ApiWritable) throw new InvalidOperationException($"API cannot write PLC-owned tag {entry.Map.Path}.");
                PlcTypes.Encode(entry.Map, write.Value, new byte[entry.Map.WireBytes]);
                if (write.RequestedApiValue is not null) PlcTypes.Encode(entry.Map, write.RequestedApiValue, new byte[entry.Map.WireBytes]);
            }
            var now = DateTimeOffset.UtcNow;
            bool changed = false;
            foreach (var (entry, write) in staged)
            {
                if (!Equals(entry.Value, write.Value))
                {
                    entry.Value = write.Value; entry.Version++; entry.LastWriter = writer; entry.ChangedAt = now;
                    changed = true;
                }
                if (fromApi) { entry.LastApiValue = TagText.Format(write.RequestedApiValue ?? write.Value); entry.LastApiWriteAt = now; }
            }
            // Repeated cyclic writes update API diagnostics but do not invalidate a script's value snapshot.
            if (changed) revision++;
            return true;
        }
    }
}
