using System.Buffers.Binary;
using System.Text;
using System.Xml.Linq;
using LogixTagRelay;
using LogixTagRelay.Design;
using LogixTagRelay.Opener;

int passed = 0;
void Check(bool condition, string name) { if (!condition) throw new Exception("FAILED: " + name); Console.WriteLine("PASS " + name); passed++; }
void Throws<T>(Action action, string name) where T : Exception
{ try { action(); } catch (T) { Check(true, name); return; } throw new Exception("Expected " + typeof(T).Name + ": " + name); }
using var fixture = File.OpenRead(Path.Combine(AppContext.BaseDirectory, "Demo.L5X"));
var catalog = L5xCatalog.Read(fixture);
var selections = new[] { new TagSelection("Status", Direction.PlcToApp), new TagSelection("Command", Direction.AppToPlc) };
var bundle = Generator.Generate(catalog, selections);
Check(bundle.Configuration.Fields.Count == 8, "UDT and array leaves expand correctly");
Check(bundle.Configuration.PlcToAppBytes == 52 && bundle.Configuration.AppToPlcBytes == 32, "directional assembly sizes");
Check(bundle.ModelCs.Contains("PlcVariable<long> Tag_Count") && bundle.ModelCs.Contains("PlcVariable<int>[] Tag_Faults"), "typed nested UDT/array model generated");
Check(SyncConfiguration.FromJson(bundle.Configuration.ToJson()).SchemaHash == bundle.Configuration.SchemaHash, "configuration consumer round trip");
Throws<FormatException>(() => SyncConfiguration.FromJson(bundle.Configuration.ToJson().Replace("\"schemaVersion\": 1", "\"schemaVersion\": 1, \"schemaVersion\": 1")), "duplicate JSON keys rejected");
Throws<FormatException>(() => (bundle.Configuration with { SchemaHash = new string('0', 64) }).Validate(), "schema drift rejected");
Throws<FormatException>(() => Generator.Generate(catalog, [new("Status", Direction.AppToPlc)]), "read-only UDT cannot become writable");
Throws<FormatException>(() => Generator.Generate(catalog, [new("Status", Direction.PlcToApp), new("Status.Speed", Direction.PlcToApp)]), "overlapping parent and child selections rejected");
Throws<ArgumentException>(() => Generator.Generate(catalog, selections, moduleName: "bad);MOV(1,X"), "ladder name injection rejected");
Throws<ArgumentException>(() => Generator.Generate(catalog with { SoftwareRevision = "unknown" }, selections), "unknown import revision is not guessed");
var xml = XDocument.Parse(bundle.RoutineL5x);
string rungs = string.Join("\n", xml.Descendants("Text").Select(x => x.Value));
Check(rungs.Contains("CPS(Status.Count,LogixRelay_PS_Tx[32],8)") && rungs.Contains("CPS(LogixRelay_PS_Rx[28],Command.Setpoint,1)"), "CPS length uses destination element units");
Check(rungs.Contains("XIC(LogixRelay_PS_ApplyCommands)") && rungs.IndexOf("Command.Setpoint", StringComparison.Ordinal) < rungs.IndexOf("MOV(LogixRelay_PS_CommandSequence", StringComparison.Ordinal), "command gate and acknowledgement ordering");
Check(xml.Root?.Attribute("TargetType")?.Value == "Routine", "L5X identifies routine import");
Check(rungs.Contains("TON(LogixRelay_PS_Watchdog,1000,0)") && rungs.Contains("NEQ(LogixRelay_PS_CommandSequence,LogixRelay_PS_AcceptedSequence)"), "PLC watchdog requires fresh generations");
var shadow = new TagNode("Status", "Program:MainProgram.Status", "DINT", "Read/Write", []);
Throws<ArgumentException>(() => Generator.Generate(catalog with { Tags = [.. catalog.Tags, shadow] }, selections), "program tag shadowing rejected");
using (var hostile = new MemoryStream(Encoding.UTF8.GetBytes("<!DOCTYPE x [<!ENTITY x SYSTEM 'file:///test'>]><RSLogix5000Content/>")))
    Throws<System.Xml.XmlException>(() => L5xCatalog.Read(hostile), "external entities prohibited");

foreach (var (type, value) in new (PlcType, object)[] { (PlcType.BOOL, true), (PlcType.SINT, (sbyte)-128),
    (PlcType.INT, short.MinValue), (PlcType.DINT, int.MinValue), (PlcType.LINT, 9007199254740993L), (PlcType.REAL, -123.75f) })
{
    var encoded = new byte[8]; PlcTypes.Encode(type, value, encoded);
    Check(Equals(value, PlcTypes.Decode(type, encoded)), "exact codec round trip " + type);
}
var model = new TestModel(bundle.Configuration);
var command = (PlcVariable<float>)model.Variables.Single(v => v.Map.Path == "Command.Setpoint");
var enable = (PlcVariable<bool>)model.Variables.Single(v => v.Map.Path == "Command.Enable");
var statusSpeed = (PlcVariable<float>)model.Variables.Single(v => v.Map.Path == "Status.Speed");
Throws<InvalidOperationException>(() => statusSpeed.Value = 1, "PLC-owned values reject app writes");
var codec = new ImplicitImage(bundle.Configuration, model);
Throws<InvalidOperationException>(() => codec.BuildAppToPlc(), "implicit defaults cannot overwrite PLC commands");
command.Value = 12.5f; enable.Value = true;
var frame = codec.BuildAppToPlc();
Check(command.Dirty && command.Quality == Quality.Unknown, "publishing an image is not PLC acceptance");
int schema = BinaryPrimitives.ReadInt32LittleEndian(Convert.FromHexString(bundle.Configuration.SchemaHash));
byte[] PlcFrame(int sequence, int ack)
{
    var result = new byte[bundle.Configuration.PlcToAppBytes];
    int[] header = [ImplicitImage.Magic, 1, schema, sequence, ack, 1];
    for (int i = 0; i < header.Length; i++) ImplicitImage.WriteInt(result, i * 4, header[i]);
    PlcTypes.Encode(PlcType.REAL, 47.25f, result.AsSpan(statusSpeed.Map.Offset));
    return result;
}
command.Value = 33f;
var now = DateTimeOffset.UtcNow;
codec.ReceivePlcToApp(PlcFrame(int.MaxValue, ImplicitImage.ReadInt(frame, 12)), now);
Check(command.Dirty && command.Value == 33f && statusSpeed.Value == 47.25f, "ack does not discard a newer app edit");
var second = codec.BuildAppToPlc();
codec.ReceivePlcToApp(PlcFrame(0, ImplicitImage.ReadInt(second, 12)), now.AddMilliseconds(1));
Check(!command.Dirty && command.Quality == Quality.Good, "sequence wrap and exact echoed acknowledgement");
codec.ReceivePlcToApp(PlcFrame(0, 0), now.AddSeconds(5));
Check(codec.CheckStale(now.AddSeconds(5), TimeSpan.FromSeconds(1)), "duplicate frames cannot refresh freshness");
var invalid = PlcFrame(1, 0); invalid[8] ^= 1;
Throws<FormatException>(() => codec.ReceivePlcToApp(invalid, now), "wrong wire schema rejected");
Check(statusSpeed.Quality == Quality.Bad, "schema failure marks stale values bad");

var transport = new FakeTransport();
foreach (var v in model.Variables) transport.Values[v.Map.Path] = v.Map.Type switch
{ PlcType.BOOL => false, PlcType.SINT => (sbyte)0, PlcType.INT => (short)0, PlcType.DINT => 0, PlcType.LINT => 0L, PlcType.REAL => 7f, _ => throw new Exception() };
command.Value = 99f;
transport.DuringWrite = () => command.Value = 100f;
var session = new ExplicitSession(bundle.Configuration, model, transport);
await session.SynchronizeOnceAsync();
Check(Equals(transport.Values[command.Map.Path], 99f) && command.Value == 100f && command.Dirty, "explicit in-flight write preserves newer edit");
transport.DuringWrite = null;
await session.SynchronizeOnceAsync();
Check(!command.Dirty && Equals(transport.Values[command.Map.Path], 100f), "next explicit cycle flushes newer edit");
command.Value = 101f; transport.FailWrites = true;
try { await session.SynchronizeOnceAsync(); throw new Exception("Expected write failure"); }
catch (IOException) { Check(command.Dirty && command.Quality == Quality.Bad, "failed explicit write remains dirty and faults the cycle"); }

transport.FailWrites = false;
var triggeredConfig = bundle.Configuration with { Explicit = new(100, "RequestSequence", 2, "AckSequence") };
transport.Values["RequestSequence"] = 42;
using (var cancel = new CancellationTokenSource())
{
    transport.OnAcknowledged = () => cancel.Cancel();
    var triggered = new ExplicitSession(triggeredConfig, model, transport);
    try { await triggered.RunAsync(cancel.Token); } catch (OperationCanceledException) { }
    Check(Equals(transport.Values["AckSequence"], 42), "trigger group echoes sequence after successful transfer");
}
Check(OpenerAdapter.NativeAbiVersion == 1, "real native DLL loads through PInvoke without opening network ports");

if (args.Length == 2 && args[0] == "--generate")
{
    Directory.CreateDirectory(args[1]);
    await File.WriteAllTextAsync(Path.Combine(args[1], "GeneratedTags.g.cs"), bundle.ModelCs, new UTF8Encoding(false));
    await File.WriteAllTextAsync(Path.Combine(args[1], "logixrelay.json"), bundle.Configuration.ToJson(), new UTF8Encoding(false));
    await File.WriteAllTextAsync(Path.Combine(args[1], "RelayRoutine.L5X"), bundle.RoutineL5x, new UTF8Encoding(false));
    await File.WriteAllTextAsync(Path.Combine(args[1], "ModuleSetup.txt"), bundle.ModuleInstructions, new UTF8Encoding(false));
}
Console.WriteLine($"{passed} checks passed. Runtime: {Environment.Version}");

sealed class TestModel : IPlcModel
{
    public IReadOnlyList<IPlcVariable> Variables { get; }
    public TestModel(SyncConfiguration config) => Variables = config.Fields.Select(f => (IPlcVariable)(f.Type switch
    {
        PlcType.BOOL => (IPlcVariable)new PlcVariable<bool>(f), PlcType.SINT => new PlcVariable<sbyte>(f), PlcType.INT => new PlcVariable<short>(f),
        PlcType.DINT => new PlcVariable<int>(f), PlcType.LINT => new PlcVariable<long>(f), PlcType.REAL => new PlcVariable<float>(f), _ => throw new Exception()
    })).ToArray();
}
sealed class FakeTransport : IExplicitTransport
{
    public Dictionary<string, object> Values { get; } = [];
    public Action? DuringWrite { get; set; }
    public Action? OnAcknowledged { get; set; }
    public bool FailWrites { get; set; }
    public ValueTask<object> ReadAsync(string path, PlcType type, CancellationToken cancellationToken) => ValueTask.FromResult(Values[path]);
    public ValueTask WriteAsync(string path, PlcType type, object value, CancellationToken cancellationToken)
    {
        if (FailWrites) throw new IOException("Injected failure");
        if (path == "Command.Setpoint") DuringWrite?.Invoke();
        Values[path] = value;
        if (path == "AckSequence") OnAcknowledged?.Invoke();
        return ValueTask.CompletedTask;
    }
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
