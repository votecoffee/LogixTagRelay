using System.Security.Cryptography;
using System.Text;

namespace LogixTagRelay.Emulation;

public sealed record MemoryTag(FieldMap Map, string Role, string Value, long Version, string LastWriter,
    DateTimeOffset? ChangedAt, string? LastApiValue, DateTimeOffset? LastApiWriteAt);
public sealed record MemorySnapshot(long Revision, IReadOnlyList<MemoryTag> Tags);
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
    public SyncConfiguration Configuration => SyncConfiguration.FromJson(config.ToJson());
    public string Fingerprint => ContractFingerprint(config);
    public static string ContractFingerprint(SyncConfiguration config)
    {
        config.Validate();
        string identity = string.Join("|", config.SchemaVersion, config.SchemaHash, config.PlcToAppBytes, config.AppToPlcBytes,
            config.Explicit?.TriggerTag?.ToUpperInvariant(), config.Explicit?.MirrorTag?.ToUpperInvariant(), config.Explicit?.ResponseTag?.ToUpperInvariant(),
            config.Implicit is not null, config.Heartbeat?.Tag.ToUpperInvariant(), config.Heartbeat?.IntervalMs);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
    }
    private Entry Find(string path) => entries.TryGetValue(path, out var entry) ? entry : throw new ArgumentException($"Unknown contract tag: {path}");
    public FieldMap Map(string path) { lock (Gate) return Find(path).Map; }
    public object Read(string path) { lock (Gate) return Find(path).Value; }
    public void SetText(string path, string value, string writer = "GUI") => Set(path, TagText.Parse(Map(path), value), writer);
    public void Set(string path, object value, string writer = "PLC") => Apply([new(path, value)], writer);
    public MemorySnapshot Snapshot()
    {
        lock (Gate) return new(revision, entries.Values.Select(e => new MemoryTag(e.Map, e.Role, TagText.Format(e.Value), e.Version,
            e.LastWriter, e.ChangedAt, e.LastApiValue, e.LastApiWriteAt)).ToArray());
    }
    /// <summary>Validate all writes before committing. Optional revision rejects a stale script transaction.</summary>
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
