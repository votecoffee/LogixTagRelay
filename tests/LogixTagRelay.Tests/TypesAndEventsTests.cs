using System.Buffers.Binary;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Text;
using LogixTagRelay;
using LogixTagRelay.Design;
using LogixTagRelay.Emulation;
using LogixTagRelay.Logix;

static class TypesAndEventsTests
{
    public static GeneratedBundle Bundle()
    {
        using var source = File.OpenRead(Path.Combine(AppContext.BaseDirectory, "Types.L5X"));
        return Generator.Generate(L5xCatalog.Read(source), [new("Telemetry", Direction.Bidirectional), new("Grid", Direction.Bidirectional)]);
    }
    public static async Task Run(Action<bool, string> check)
    {
        var bundle = Bundle(); var config = bundle.Configuration;
        object[] values = [(byte)255, ushort.MaxValue, uint.MaxValue, ulong.MaxValue, new PlcDateTime(1_793_620_800_123_456), new PlcTime(-9_007_199_254_740_993)];
        var fields = config.Fields.Take(6).ToArray();
        for (int i = 0; i < fields.Length; i++)
        {
            var field = fields[i]; var bytes = new byte[field.WireBytes];
            PlcTypes.Encode(field, values[i], bytes);
            check(Equals(PlcTypes.Decode(field, bytes), values[i]), "exact boundary codec " + field.Type);
            check(Equals(TagText.Parse(field, TagText.Format(values[i])), values[i]), "lossless text boundary " + field.Type);
        }
        check(bundle.ModelCs.Contains("PlcVariable<PlcDateTime>") && bundle.ModelCs.Contains("PlcVariable<PlcTime>") && bundle.ModelCs.Contains("PlcVariable<uint>[,]") && config.Fields.Count == 10,
            "DT TIME unsigned and multidimensional arrays generate typed contracts");
        check(bundle.RoutineL5x.Contains("MOV(Telemetry.StartedAt,LogixRelay_PS_TimeBits)") && bundle.RoutineL5x.Contains("MOV(LogixRelay_PS_TimeBits,Telemetry.Elapsed)") &&
            !bundle.RoutineL5x.Contains("CPS(Telemetry.StartedAt,"), "generated PLC routine bridges DT/TIME through LINT before byte copies");
        var date = new DateTimeOffset(2026, 11, 2, 12, 0, 0, TimeSpan.FromHours(2)).AddTicks(1_234_560);
        check(PlcDateTime.FromDateTimeOffset(date).ToDateTimeOffset() == date, "DT conversion preserves UTC instant and microseconds");
        check(PlcTime.FromTimeSpan(TimeSpan.FromTicks(-1_234_560)).ToTimeSpan().Ticks == -1_234_560, "TIME conversion preserves signed microseconds");
        bool Reject(Action action) { try { action(); return false; } catch (ArgumentException) { return true; } catch (OverflowException) { return true; } }
        check(Reject(() => PlcDateTime.FromDateTimeOffset(date.AddTicks(1))) && Reject(() => PlcTime.FromTimeSpan(TimeSpan.FromTicks(1))), "sub-microsecond conversions reject precision loss");
        check(Reject(() => new PlcTime(long.MaxValue).ToTimeSpan()) && Reject(() => new PlcDateTime(long.MinValue).ToDateTimeOffset()), "out-of-range .NET conversions fail without corrupting the raw value");
        check(Reject(() => TagText.Parse(fields[0], "256")) && Reject(() => TagText.Parse(fields[3], "-1")), "unsigned boundaries reject overflow and negative input");

        var model = new ObservableModel(config); var memory = new PlcMemory(config);
        int changes = 0, properties = 0, collections = 0;
        var events = new List<PlcValueChangedEventArgs>();
        model.TagValueChanged += (_, e) => { changes++; events.Add(e); };
        model.PropertyChanged += (_, _) => properties++;
        ((INotifyCollectionChanged)model.Variables).CollectionChanged += (_, _) => collections++;
        for (int i = 0; i < fields.Length; i++) memory.Set(fields[i].Path, values[i]);
        await using var transport = new MemoryExplicitTransport(memory);
        var session = new ExplicitSession(config, model, transport);
        await session.SynchronizeOnceAsync();
        check(changes == 6 && properties == 6 && events.All(e => e.Origin == ValueChangeOrigin.Plc), "explicit reads emit one contract event per changed value");
        int variableProperties = 0;
        foreach (IObservablePlcVariable variable in model.Variables) variable.PropertyChanged += (_, _) => variableProperties++;
        await session.SynchronizeOnceAsync(); await session.SynchronizeOnceAsync();
        check(changes == 6 && properties == 6 && variableProperties == 0, "identical explicit reads emit no value property or collection events");
        var code = (PlcVariable<byte>)model.Variables[0]; code.Value = 42; code.Value = 42;
        check(changes == 7 && events[^1].Path == "Telemetry.Code" && events[^1].Origin == ValueChangeOrigin.Application && (byte)events[^1].OldValue == 255 && (byte)events[^1].NewValue == 42,
            "local edits report old/new values once with their PLC path");
        await session.SynchronizeOnceAsync(); await session.SynchronizeOnceAsync();
        check(changes == 7 && !code.Dirty && (byte)memory.Read(code.Map.Path) == 42 && collections == 0 && ReferenceEquals(model.Variables, model.Variables),
            "write acceptance and stable collection membership do not fabricate value events");
        int failures = 0, survivingHandlers = 0;
        code.ValueChanged += (_, _) => throw new InvalidOperationException("Test observer");
        code.ValueChanged += (_, _) => survivingHandlers++;
        model.NotificationFailed += (_, _) => failures++;
        memory.Set(code.Map.Path, (byte)43); await session.SynchronizeOnceAsync();
        check(failures == 1 && survivingHandlers == 1 && code.Quality == Quality.Good, "subscriber exceptions are reported without interrupting synchronization or later handlers");
        var command = new PlcVariable<int>(new("Command", PlcType.DINT, Direction.AppToPlc, 24));
        int commands = 0, observations = 0;
        command.ValueChanged += (_, _) => commands++;
        command.PropertyChanged += (_, e) => { if (e.PropertyName == "ObservedValue") observations++; };
        command.Observe(10, DateTimeOffset.UtcNow); command.Observe(10, DateTimeOffset.UtcNow); command.Observe(11, DateTimeOffset.UtcNow);
        check(commands == 0 && observations == 2 && command.Value == 0, "command readback only notifies observed-value changes");
        var initial = new PlcVariable<int>(new("Initial", PlcType.DINT, Direction.Bidirectional, 24)); int defaults = 0;
        initial.ValueChanged += (_, _) => defaults++; initial.Value = 0;
        check(initial.Dirty && defaults == 0, "intentional first default assignment is dirty without inventing a value change");

        var implicitMemory = new PlcMemory(config); var implicitModel = new ObservableModel(config);
        var plc = new ImplicitPlc(implicitMemory); var codec = new ImplicitImage(config, implicitModel);
        int implicitEvents = 0; implicitModel.TagValueChanged += (_, _) => implicitEvents++;
        codec.ReceivePlcToApp(plc.Output(), DateTimeOffset.UtcNow);
        for (int i = 0; i < fields.Length; i++) Set(implicitModel.Variables[i], values[i]);
        var reply = plc.Exchange(codec.BuildAppToPlc()); codec.ReceivePlcToApp(reply, DateTimeOffset.UtcNow);
        check(fields.Select((f, i) => Equals(implicitMemory.Read(f.Path), values[i])).All(x => x) && implicitModel.Variables.All(v => !v.Dirty),
            "all new scalar types round trip through the implicit byte merge");
        int before = implicitEvents;
        codec.ReceivePlcToApp(reply, DateTimeOffset.UtcNow);
        codec.ReceivePlcToApp(plc.Exchange(codec.BuildAppToPlc()), DateTimeOffset.UtcNow);
        check(implicitEvents == before, "duplicate and unchanged fresh implicit images do not raise value events");
        Set(implicitModel.Variables[0], (byte)1); implicitMemory.Set(fields[0].Path, (byte)7);
        codec.ReceivePlcToApp(plc.Exchange(codec.BuildAppToPlc()), DateTimeOffset.UtcNow);
        check((byte)implicitModel.Variables[0].BoxedValue == 7 && implicitEvents == before + 2, "PLC-winning implicit merge notifies the actual replacement value");

        var scripts = new ScriptRunner(memory);
        scripts.Configure("plc.set('Telemetry.Count64', 18446744073709551615n); plc.set('Telemetry.Elapsed', -9007199254740993n); plc.set('Telemetry.StartedAt', 1793620800123456n); plc.set('Grid[1,1]', 4294967295);");
        check(scripts.Step() && (ulong)memory.Read("Telemetry.Count64") == ulong.MaxValue && (PlcTime)memory.Read("Telemetry.Elapsed") == (PlcTime)values[5] && (uint)memory.Read("Grid[1,1]") == uint.MaxValue,
            "JavaScript preserves unsigned 64-bit and temporal BigInt values");
        scripts.Configure("plc.set('Telemetry.Code', 8); plc.set('Telemetry.Count64', -1n);");
        check(!scripts.Step() && (byte)memory.Read("Telemetry.Code") == 43, "invalid unsigned script tick rolls back every staged edit");
        await NativeSnapshot(fields, values, check);
        var rll = new RllSubset(bundle); rll.Set("LogixRelay_PS_ApplyCommands", 1); rll.Scan();
        var rllModel = new TestModel(config); var rllCodec = new ImplicitImage(config, rllModel);
        rllCodec.ReceivePlcToApp(rll.Bytes("LogixRelay:O.Data"), DateTimeOffset.UtcNow);
        for (int i = 0; i < fields.Length; i++) Set(rllModel.Variables[i], values[i]);
        rll.Bytes("LogixRelay:I.Data", rllCodec.BuildAppToPlc()); rll.Scan();
        check(fields.Select((f, i) => Equals(PlcTypes.Decode(f, rll.Bytes(f.Path)), values[i])).All(x => x), "emitted PLC wrapper executes unsigned and temporal byte copies in the offline instruction model");
    }
    public static void Set(IPlcVariable variable, object value)
    {
        switch (variable)
        {
            case PlcVariable<byte> v: v.Value = (byte)value; break;
            case PlcVariable<ushort> v: v.Value = (ushort)value; break;
            case PlcVariable<uint> v: v.Value = (uint)value; break;
            case PlcVariable<ulong> v: v.Value = (ulong)value; break;
            case PlcVariable<PlcDateTime> v: v.Value = (PlcDateTime)value; break;
            case PlcVariable<PlcTime> v: v.Value = (PlcTime)value; break;
            default: throw new ArgumentException("Unexpected test variable");
        }
    }
    private sealed class ObservableModel : PlcModel
    {
        public ObservableModel(SyncConfiguration config) => InitializeVariables(new TestModel(config).Variables.ToArray(), config.Fields.Select(_ => "Telemetry").ToArray());
    }
    private static async Task NativeSnapshot(FieldMap[] fields, object[] values, Action<bool, string> check)
    {
        int[] offsets = [0, 2, 4, 8, 16, 24], codes = [0xC6, 0xC7, 0xC8, 0xC9, 0xC0, 0xD7];
        var symbol = new byte[31]; BinaryPrimitives.WriteUInt16LittleEndian(symbol.AsSpan(4), 0x8001);
        BinaryPrimitives.WriteUInt16LittleEndian(symbol.AsSpan(20), 9); Encoding.ASCII.GetBytes("Telemetry").CopyTo(symbol, 22);
        var names = Encoding.ASCII.GetBytes("TelemetryData;\0" + string.Join("\0", fields.Select(f => f.Path.Split('.')[1])) + "\0");
        var template = new byte[14 + 6 * 8 + names.Length];
        BinaryPrimitives.WriteUInt16LittleEndian(template, 1); BinaryPrimitives.WriteInt32LittleEndian(template.AsSpan(6), 32); BinaryPrimitives.WriteUInt16LittleEndian(template.AsSpan(10), 6);
        var image = new byte[32];
        for (int i = 0; i < 6; i++)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(template.AsSpan(14 + i * 8 + 2), (ushort)codes[i]);
            BinaryPrimitives.WriteInt32LittleEndian(template.AsSpan(14 + i * 8 + 4), offsets[i]);
            PlcTypes.Encode(fields[i], values[i], image.AsSpan(offsets[i]));
        }
        names.CopyTo(template, 62);
        var raw = new Dictionary<string, byte[]> { ["@tags"] = symbol, ["@udt/1"] = template, ["Telemetry"] = image };
        var reader = new LogixUdtReader((path, _) => ValueTask.FromResult(raw[path]));
        var decoded = await reader.ReadAsync(new("Telemetry", "TelemetryData", fields.Select(f => f.Path).ToList()), fields);
        check(fields.Select((f, i) => Equals(decoded[f.Path], values[i])).All(x => x), "native UDT descriptors decode DT TIME and unsigned CIP types at their native offsets");
    }
}
