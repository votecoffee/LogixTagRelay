using System.Text;
using LogixTagRelay;
using LogixTagRelay.Design;
using LogixTagRelay.Emulation;

static class CommonTypesTests
{
    public static async Task Run(Action<bool, string> check)
    {
        var cases = new (PlcType Type, object Value)[] { (PlcType.LREAL, double.MaxValue), (PlcType.LREAL, double.NaN),
            (PlcType.TIME32, new PlcTime32(int.MinValue)), (PlcType.LTIME, new PlcLongTime(long.MinValue)), (PlcType.LDT, new PlcLongDateTime(long.MaxValue)) };
        foreach (var (type, value) in cases)
        {
            var field = new FieldMap("Value", type, Direction.Bidirectional, 24); var bytes = new byte[field.WireBytes];
            PlcTypes.Encode(field, value, bytes);
            check(Equals(value, PlcTypes.Decode(field, bytes)) && Equals(value, TagText.Parse(field, TagText.Format(value))), "exact new common type codec/text " + type + " " + TagText.Format(value));
            var config = new SyncConfiguration(SyncConfiguration.ComputeHash([field]), [field], 24 + field.WireBytes, 24 + field.WireBytes, Explicit: new(), Implicit: new());
            var memory = new PlcMemory(config); memory.Set("Value", value); var model = new TestModel(config);
            await using var transport = new MemoryExplicitTransport(memory); await new ExplicitSession(config, model, transport).SynchronizeOnceAsync();
            var codec = new ImplicitImage(config, model); var plc = new ImplicitPlc(memory); codec.ReceivePlcToApp(plc.Output(), DateTimeOffset.UtcNow);
            codec.ReceivePlcToApp(plc.Exchange(codec.BuildAppToPlc()), DateTimeOffset.UtcNow);
            check(Equals(model.Variables[0].BoxedValue, value), "explicit and implicit common type " + type);
        }
        check(PlcTime32.FromTimeSpan(TimeSpan.FromTicks(-12340)).Microseconds == -1234 && PlcLongTime.FromTimeSpan(TimeSpan.FromTicks(5)).Nanoseconds == 500,
            "TIME32 and nanosecond conversion helpers preserve units");
        bool rejects = false; try { new PlcLongTime(1).ToTimeSpan(); } catch (InvalidOperationException) { rejects = true; }
        check(rejects, "nanosecond helpers reject sub-tick precision loss");
        using var source = new MemoryStream(Encoding.UTF8.GetBytes("""
            <RSLogix5000Content SoftwareRevision="35.03"><Controller Name="Builtins"><Tags>
            <Tag Name="Timer1" TagType="Base" DataType="TIMER" ExternalAccess="Read/Write"/>
            <Tag Name="Counter1" TagType="Base" DataType="COUNTER" ExternalAccess="Read/Write"/>
            <Tag Name="Nano" TagType="Base" DataType="LTIME" ExternalAccess="Read/Write"/>
            <Tag Name="ShortTime" TagType="Base" DataType="TIME32" ExternalAccess="Read/Write"/>
            </Tags></Controller></RSLogix5000Content>
            """));
        var bundle = Generator.Generate(L5xCatalog.Read(source), [new("Timer1", Direction.Bidirectional), new("Counter1", Direction.Bidirectional), new("Nano", Direction.Bidirectional), new("ShortTime", Direction.Bidirectional)]);
        check(bundle.Configuration.Fields.Count == 14 && bundle.Configuration.Fields.Single(f => f.Path == "Timer1.DN").Direction == Direction.PlcToApp &&
            bundle.Configuration.Fields.Single(f => f.Path == "Counter1.PRE").CanWrite && bundle.Configuration.Explicit!.ReadGroups!.Count == 0,
            "TIMER COUNTER expand accessible members with read-only instruction flags and individual explicit reads");
        check(bundle.ModelCs.Contains("PlcVariable<PlcLongTime>") && bundle.RoutineL5x.Contains("_Time32Bits") && !bundle.RoutineL5x.Contains("CPS(Nano,"), "common temporal generation uses exact types and LINT bridges");
    }
}
