using LogixTagRelay;
using LogixTagRelay.Emulation;
using LogixTagRelay.Generated;

var config = SyncConfiguration.FromJson(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory,"logixrelay.json")));
var tags = new GeneratedTags();
var plc = new PlcMemory(config);
plc.Set("Machine.Setpoint",10f); plc.Set("Machine.Actual",7f); plc.Set("Machine.Counts[2]",3);
await using var transport = new TracedTransport(new MemoryExplicitTransport(plc));
var session = new ExplicitSession(config,tags,transport);
await session.SynchronizeOnceAsync();
Console.WriteLine($"Initial UDT read: Setpoint={tags.Tag_Machine.Tag_Setpoint.Value}, Actual={tags.Tag_Machine.Tag_Actual.Value}; writes={transport.Writes.Count}");

plc.Set("Machine.Counts[2]",99); // Simulated independent PLC edit.
tags.Tag_Machine.Tag_Setpoint.Value = 25f; // Only this member is locally changed.
await session.SynchronizeOnceAsync();
Console.WriteLine($"Next UDT read: PLC Counts[2]={tags.Tag_Machine.Tag_Counts[2].Value}; API Setpoint={plc.Read("Machine.Setpoint")}");
Console.WriteLine($"Member writes: {string.Join(", ",transport.Writes)}");
tags.Tag_Machine.Tag_Setpoint.Value = 25f;
await session.SynchronizeOnceAsync();
if (!transport.Writes.SequenceEqual(new[] { "Machine.Setpoint" }) || tags.Tag_Machine.Tag_Counts[2].Value != 99 || tags.Tag_Machine.Tag_Setpoint.Dirty)
    throw new Exception("Dirty-member example failed.");
Console.WriteLine("Clean cycle and unchanged assignment: no additional writes. Read-only members remain protected.");

sealed class TracedTransport(IExplicitTransport inner) : IExplicitTransport
{
    public List<string> Writes { get; } = [];
    public ValueTask<object> ReadAsync(string path,PlcType type,CancellationToken ct) => inner.ReadAsync(path,type,ct);
    public ValueTask WriteAsync(string path,PlcType type,object value,CancellationToken ct) { Writes.Add(path); return inner.WriteAsync(path,type,value,ct); }
    public ValueTask<object> ReadAsync(FieldMap field,CancellationToken ct) => inner.ReadAsync(field,ct);
    public ValueTask WriteAsync(FieldMap field,object value,CancellationToken ct) { Writes.Add(field.Path); return inner.WriteAsync(field,value,ct); }
    public ValueTask<IReadOnlyList<object>> ReadBatchAsync(IReadOnlyList<FieldMap> fields,IReadOnlyList<ExplicitReadGroup> groups,CancellationToken ct) => inner.ReadBatchAsync(fields,groups,ct);
    public ValueTask DisposeAsync() => inner.DisposeAsync();
}
