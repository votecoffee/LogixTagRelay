using System.Buffers.Binary;
using System.Text;
using LogixTagRelay;
using LogixTagRelay.Design;
using LogixTagRelay.Emulation;
using LogixTagRelay.Logix;

static class BidirectionalTests
{
    public static GeneratedBundle Bundle()
    {
        using var source = File.OpenRead(Path.Combine(AppContext.BaseDirectory, "Bidirectional.L5X"));
        return Generator.Generate(L5xCatalog.Read(source), [new("Machine", Direction.Bidirectional)]);
    }
    public static async Task Run(Action<bool,string> check)
    {
        var bundle = Bundle(); var config = bundle.Configuration;
        check(config.Fields.Count(f => f.Direction == Direction.Bidirectional) == 7 && config.Fields.Count(f => f.Direction == Direction.PlcToApp) == 2,
            "whole bidirectional UDT retains read-only leaf permissions");
        check(config.Explicit?.ReadGroups is [{ Path: "Machine", DataType: "MachineData" }] && config.Explicit.ReadGroups[0].Fields.Count == 9,
            "generator retains an explicit whole-UDT read group alongside member mappings");
        check(SyncConfiguration.FromJson(config.ToJson()).Explicit!.ReadGroups![0].Path == "Machine", "bidirectional UDT read settings round trip through JSON");
        var memory = new PlcMemory(config); memory.Set("Machine.Setpoint",10f); memory.Set("Machine.Actual",7f);
        var model = new TestModel(config); var value = (PlcVariable<float>)model.Variables.Single(v => v.Map.Path == "Machine.Setpoint");
        await using var trace = new Trace(new MemoryExplicitTransport(memory)); var session = new ExplicitSession(config,model,trace);
        await session.SynchronizeOnceAsync();
        check(value.Value == 10f && !value.Dirty && trace.Writes.Count == 0 && trace.Batches == 1, "clean bidirectional values follow PLC reads without becoming dirty or writing defaults");
        memory.Set("Machine.Setpoint",11f); await session.SynchronizeOnceAsync();
        check(value.Value == 11f && trace.Writes.Count == 0, "later PLC edits appear in normal bidirectional Value");
        value.Value = 25f; memory.Set("Machine.Counts[2]",99); await session.SynchronizeOnceAsync();
        check(trace.Writes.SequenceEqual(new[] { "Machine.Setpoint" }) && (int)memory.Read("Machine.Counts[2]") == 99 &&
            (int)model.Variables.Single(v => v.Map.Path == "Machine.Counts[2]").BoxedValue == 99 && value.Value == 25f && !value.Dirty,
            "cyclic UDT read preserves pending app edit and writes only its changed member");
        long version = value.Version; value.Value = 25f; await session.SynchronizeOnceAsync();
        check(value.Version == version && trace.Writes.Count == 1, "same-value setter and clean cycles produce no redundant member writes");
        value.Value = 26f; value.Value = 25f; await session.SynchronizeOnceAsync();
        check(!value.Dirty && trace.Writes.Count == 1, "reverted edit already matching the PLC clears without a write");
        value.Value = 30f; trace.DuringWrite = () => value.Value = 31f; await session.SynchronizeOnceAsync(); trace.DuringWrite = null;
        check(value.Dirty && value.Value == 31f && (float)memory.Read(value.Map.Path) == 30f, "bidirectional member write acknowledgement retains a newer local edit");
        await session.SynchronizeOnceAsync();
        check(!value.Dirty && (float)memory.Read(value.Map.Path) == 31f, "next UDT cycle flushes the newer member edit");
        value.Value = 32f; trace.Fail = true;
        try { await session.SynchronizeOnceAsync(); throw new Exception("Expected failure"); } catch (IOException) { }
        check(value.Dirty && value.Quality == Quality.Bad, "failed bidirectional write stays dirty and exposes bad quality");
        trace.Fail = false;
        var initial = new TestModel(config); var initialValue = (PlcVariable<float>)initial.Variables.Single(v => v.Map.Path == "Machine.Setpoint"); initialValue.Value = 0f;
        await new ExplicitSession(config,initial,trace).SynchronizeOnceAsync();
        check((float)memory.Read("Machine.Setpoint") == 0f, "intentional pre-read assignment of CLR default is retained");
        try { ((PlcVariable<float>)initial.Variables.Single(v => v.Map.Path == "Machine.Actual")).Value = 2f; throw new Exception("Read-only setter accepted"); }
        catch (InvalidOperationException) { check(true,"read-only members of a bidirectional UDT still reject app setters"); }

        var implicitModel = new TestModel(config); var codec = new ImplicitImage(config,implicitModel); var plc = new ImplicitPlc(memory);
        memory.Set("Machine.Setpoint",1f); codec.ReceivePlcToApp(plc.Exchange(codec.BuildBootstrap()),DateTimeOffset.UtcNow);
        var shared = (PlcVariable<float>)implicitModel.Variables.Single(v => v.Map.Path == "Machine.Setpoint"); shared.Value = BitConverter.Int32BitsToSingle(0x3f810002);
        memory.Set("Machine.Setpoint",BitConverter.Int32BitsToSingle(0x3f800001));
        codec.ReceivePlcToApp(plc.Exchange(codec.BuildAppToPlc()),DateTimeOffset.UtcNow);
        check(BitConverter.SingleToInt32Bits(shared.Value) == 0x3f810001 && !shared.Dirty, "implicit bidirectional tags keep the type-ignorant PLC-first merge rule");
        check(bundle.RoutineL5x.Contains("Machine.Setpoint,1)") && !bundle.RoutineL5x.Contains("Machine.Actual,1)"), "generated PLC wrapper applies bidirectional members and protects read-only leaves");
        await NativeSnapshot(config,check);
        check(LogixAddress.SymbolicPath(new("Flags[33]",PlcType.BOOL,Direction.Bidirectional,24)) == "Flags[1].1" &&
            LogixAddress.SymbolicPath(new("Machine.Flags[63]",PlcType.BOOL,Direction.Bidirectional,24)) == "Machine.Flags[1].31", "explicit BOOL array elements use masked bit addresses instead of whole-word writes");
    }
    private static async Task NativeSnapshot(SyncConfiguration config, Action<bool,string> check)
    {
        byte[] Template(int id, string name, int size, params (string Name, int Type, int Meta, int Offset)[] members)
        {
            byte[] names = Encoding.ASCII.GetBytes(name + ";\0" + string.Join("\0",members.Select(m => m.Name)) + "\0");
            var bytes = new byte[14 + members.Length*8 + names.Length];
            BinaryPrimitives.WriteUInt16LittleEndian(bytes,(ushort)id); BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(6),size); BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(10),(ushort)members.Length);
            for (int i=0;i<members.Length;i++) { int at=14+i*8; BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(at),(ushort)members[i].Meta); BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(at+2),(ushort)members[i].Type); BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(at+4),members[i].Offset); }
            names.CopyTo(bytes,14+members.Length*8); return bytes;
        }
        byte[] symbol = new byte[29]; BinaryPrimitives.WriteUInt16LittleEndian(symbol.AsSpan(4),0x8001); BinaryPrimitives.WriteUInt16LittleEndian(symbol.AsSpan(20),7); Encoding.ASCII.GetBytes("Machine").CopyTo(symbol,22);
        byte[] image = new byte[48]; BinaryPrimitives.WriteSingleLittleEndian(image,2f); BinaryPrimitives.WriteSingleLittleEndian(image.AsSpan(4),7f); image[8]=2;
        for(int i=0;i<3;i++) BinaryPrimitives.WriteInt32LittleEndian(image.AsSpan(12+i*4),7+i);
        var label = config.Fields.Single(f => f.Path == "Machine.Label"); PlcTypes.Encode(label,"A\0é",image.AsSpan(24));
        BinaryPrimitives.WriteSingleLittleEndian(image.AsSpan(40),12.5f); image[44]=8;
        var raw = new Dictionary<string,byte[]> {
            ["@tags"] = symbol, ["Machine"] = image,
            ["@udt/1"] = Template(1,"MachineData",48,("Setpoint",0xCA,0,0),("Actual",0xCA,0,4),("Enabled",0xC1,1,8),("ZZZZ",0xC2,0,8),("Counts",0x20C4,3,12),("Label",0x8002,0,24),("Axis",0x8003,0,40)),
            ["@udt/2"] = Template(2,"Text12",16,("LEN",0xC4,0,0),("DATA",0x20C2,12,4)),
            ["@udt/3"] = Template(3,"AxisData",8,("Position",0xCA,0,0),("Ready",0xC1,3,4)) };
        var calls = new List<string>(); var reader = new LogixUdtReader((path,ct) => { calls.Add(path); return ValueTask.FromResult(raw[path].ToArray()); });
        var group = config.Explicit!.ReadGroups![0];
        var values = await reader.ReadAsync(group,config.Fields);
        check((float)values["Machine.Setpoint"] == 2f && (bool)values["Machine.Enabled"] && (int)values["Machine.Counts[2]"] == 9 && (string)values["Machine.Label"] == "A\0é" && (float)values["Machine.Axis.Position"] == 12.5f && (bool)values["Machine.Axis.Ready"],
            "native UDT snapshot decodes controller offsets, packed BOOLs, member arrays, custom STRING and nested UDT");
        BinaryPrimitives.WriteSingleLittleEndian(image,5f); values = await reader.ReadAsync(group,config.Fields);
        check((float)values["Machine.Setpoint"] == 5f && calls.Count(p => p == "Machine") == 2 && calls.Count(p => p.StartsWith('@')) == 4 && !calls.Any(p => p.StartsWith("Machine.")),
            "cyclic native UDT reads refresh the root once per cycle and cache introspection without reading individual members");
        raw["Machine"] = new byte[44];
        try { await reader.ReadAsync(group,config.Fields); throw new Exception("Truncated UDT accepted"); } catch (FormatException) { check(true,"native UDT size drift fails before any writes"); }
    }
    private sealed class Trace(IExplicitTransport inner) : IExplicitTransport
    {
        public List<string> Writes { get; } = [];
        public int Batches { get; private set; }
        public Action? DuringWrite { get; set; }
        public bool Fail { get; set; }
        public ValueTask<object> ReadAsync(string p,PlcType t,CancellationToken ct) => inner.ReadAsync(p,t,ct);
        public async ValueTask WriteAsync(string p,PlcType t,object value,CancellationToken ct) { if(Fail) throw new IOException("Injected"); DuringWrite?.Invoke(); Writes.Add(p); await inner.WriteAsync(p,t,value,ct); }
        public ValueTask<object> ReadAsync(FieldMap f,CancellationToken ct) => inner.ReadAsync(f,ct);
        public async ValueTask WriteAsync(FieldMap f,object value,CancellationToken ct) { if(Fail) throw new IOException("Injected"); DuringWrite?.Invoke(); Writes.Add(f.Path); await inner.WriteAsync(f,value,ct); }
        public ValueTask<IReadOnlyList<object>> ReadBatchAsync(IReadOnlyList<FieldMap> f,IReadOnlyList<ExplicitReadGroup> g,CancellationToken ct) { Batches++; return inner.ReadBatchAsync(f,g,ct); }
        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }
}
