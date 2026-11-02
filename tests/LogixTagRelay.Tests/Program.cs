using System.Buffers.Binary;
using System.Text;
using System.Xml.Linq;
using LogixTagRelay;
using LogixTagRelay.Design;
using LogixTagRelay.Opener;
using LogixTagRelay.Logix;

int passed = 0;
void Check(bool condition, string name) { if (!condition) throw new Exception("FAILED: " + name); Console.WriteLine("PASS " + name); passed++; }
void Throws<T>(Action action, string name) where T : Exception
{ try { action(); } catch (T) { Check(true, name); return; } throw new Exception("Expected " + typeof(T).Name + ": " + name); }
using var fixture = File.OpenRead(Path.Combine(AppContext.BaseDirectory, "Demo.L5X"));
var catalog = L5xCatalog.Read(fixture);
var selections = new[] { new TagSelection("Status", Direction.PlcToApp), new TagSelection("Command", Direction.AppToPlc) };
var bundle = Generator.Generate(catalog, selections);
Check(bundle.Configuration.Fields.Count == 8, "UDT and array leaves expand correctly");
Check(bundle.Configuration.PlcToAppBytes == 60 && bundle.Configuration.AppToPlcBytes == 60, "symmetric complete assembly sizes");
Check(bundle.ModelCs.Contains("PlcVariable<long> Tag_Count") && bundle.ModelCs.Contains("PlcVariable<int>[] Tag_Faults"), "typed nested UDT/array model generated");
Check(SyncConfiguration.FromJson(bundle.Configuration.ToJson()).SchemaHash == bundle.Configuration.SchemaHash, "configuration consumer round trip");
Throws<FormatException>(() => SyncConfiguration.FromJson(bundle.Configuration.ToJson().Replace("\"schemaVersion\": 2", "\"schemaVersion\": 2, \"schemaVersion\": 2")), "duplicate JSON keys rejected");
Throws<FormatException>(() => (bundle.Configuration with { SchemaHash = new string('0', 64) }).Validate(), "schema drift rejected");
Throws<FormatException>(() => Generator.Generate(catalog, [new("Status", Direction.AppToPlc)]), "read-only UDT cannot become writable");
Throws<FormatException>(() => Generator.Generate(catalog, [new("Status", Direction.PlcToApp), new("Status.Speed", Direction.PlcToApp)]), "overlapping parent and child selections rejected");
Throws<ArgumentException>(() => Generator.Generate(catalog, selections, moduleName: "bad);MOV(1,X"), "ladder name injection rejected");
Throws<ArgumentException>(() => Generator.Generate(catalog with { SoftwareRevision = "unknown" }, selections), "unknown import revision is not guessed");
var xml = XDocument.Parse(bundle.RoutineL5x);
string rungs = string.Join("\n", xml.Descendants("Text").Select(x => x.Value));
Check(rungs.Contains("CPS(Status.Count,LogixRelay_PS_Tx[32],8)") && rungs.Contains("CPS(LogixRelay_PS_Current[32],Command.Setpoint,1)"), "CPS length uses destination element units");
Check(rungs.Contains("XIC(LogixRelay_PS_ApplyCommands)") && rungs.IndexOf("Command.Setpoint", StringComparison.Ordinal) < rungs.IndexOf("MOV(LogixRelay_PS_CommandSequence", StringComparison.Ordinal), "command gate and acknowledgement ordering");
Check(xml.Root?.Attribute("TargetType")?.Value == "Routine", "L5X identifies routine import");
Check(xml.Descendants("AddOnInstructionDefinition").Single().Attribute("Name")?.Value == SintMergeAoi.Name &&
    bundle.MergeAoiL5x.Contains("TargetType=\"AddOnInstructionDefinition\"") && bundle.MergeStructuredText == SintMergeAoi.StructuredText,
    "generated bundle embeds the ST AOI dependency and supplies standalone review sources");
var triggerBundle = Generator.Generate(catalog, selections, explicitOptions: new(TriggerTag: "RequestSequence", ResponseTag: "AckSequence"));
Check(triggerBundle.Configuration.Explicit?.ResponseTag == "AckSequence", "contract generation retains DINT trigger and response controls");
Throws<ArgumentException>(() => Generator.Generate(catalog, selections, explicitOptions: new(TriggerTag: "Missing")), "unknown trigger catalog tag rejected");
Throws<ArgumentException>(() => Generator.Generate(catalog, [new("Command", Direction.AppToPlc)], explicitOptions: new(TriggerTag: "Status.Speed")), "non-DINT trigger catalog tag rejected");
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
Throws<InvalidOperationException>(() => codec.BuildAppToPlc(), "implicit commands require a PLC baseline");
command.Value = 12.5f; enable.Value = true;
int schema = BinaryPrimitives.ReadInt32LittleEndian(Convert.FromHexString(bundle.Configuration.SchemaHash));
byte[] PlcFrame(int sequence, int ack)
{
    var result = new byte[bundle.Configuration.PlcToAppBytes];
    int[] header = [ImplicitImage.Magic, ImplicitImage.ProtocolVersion, schema, sequence, ack, 1];
    for (int i = 0; i < header.Length; i++) ImplicitImage.WriteInt(result, i * 4, header[i]);
    PlcTypes.Encode(PlcType.REAL, 47.25f, result.AsSpan(statusSpeed.Map.Offset));
    return result;
}
var now = DateTimeOffset.UtcNow;
codec.ReceivePlcToApp(PlcFrame(int.MaxValue - 1, 0), now);
var frame = codec.BuildAppToPlc();
Check(command.Dirty && command.Quality == Quality.Unknown, "publishing an image is not PLC acceptance");
command.Value = 33f;
codec.ReceivePlcToApp(PlcFrame(int.MaxValue, ImplicitImage.ReadInt(frame, 12)), now);
Check(command.Dirty && command.Value == 33f && statusSpeed.Value == 47.25f, "ack does not discard a newer app edit");
var second = codec.BuildAppToPlc();
codec.ReceivePlcToApp(PlcFrame(1, ImplicitImage.ReadInt(second, 12)), now.AddMilliseconds(1));
Check(!command.Dirty && command.Quality == Quality.Good, "sequence wrap and exact echoed acknowledgement");
codec.ReceivePlcToApp(PlcFrame(1, 0), now.AddSeconds(5));
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
await TriggerTests.Run(bundle.Configuration, Check);
await EmulationTests.Run(bundle, Check);
await BidirectionalTests.Run(Check);
await TypesAndEventsTests.Run(Check);
var aoiXml = XDocument.Parse(SintMergeAoi.GenerateL5x());
Check(aoiXml.Descendants("Parameter").Where(p => (string?)p.Attribute("Usage") == "InOut").Count() == 3 &&
    aoiXml.Descendants("Parameter").Where(p => (string?)p.Attribute("Usage") == "InOut").All(p => (string?)p.Attribute("DataType") == "SINT" && (string?)p.Attribute("Dimensions") == "1"), "merge AOI exposes exactly three arbitrary-size SINT InOut arrays");
var st = new StSubset(SintMergeAoi.StructuredText);
sbyte[] saved = [1, 1, 1, 1], plc = [1, 2, 1, 4], api = [9, 3, 1, 4];
var merged = st.Run(saved, plc, api);
Check(plc.SequenceEqual(new sbyte[] { 9, 2, 1, 4 }) && saved.SequenceEqual(new sbyte[] { 1,1,1,1 }) && api.SequenceEqual(new sbyte[] { 9,3,1,4 }), "emitted ST applies API bytes only where PLC still matches baseline");
Check(merged.Get("ConflictBytes") == 1 && merged.Get("AppliedBytes") == 1 && merged.Get("Error") == 0, "merge AOI reports conflicts and applied bytes");
var mismatch = st.Run([1], [2,3], [4,5]);
Check(mismatch.Get("Error") == 1 && mismatch.Arrays["Current"].SequenceEqual(new sbyte[] { 2,3 }), "merge size mismatch returns an error without changing PLC bytes");
var random = new Random(20261102);
foreach (int size in new[] { 1, 2, 7, 480, 2048 })
{
    var oldBytes = Enumerable.Range(0, size).Select(_ => (sbyte)random.Next(-128, 128)).ToArray();
    var currentBytes = oldBytes.Select(b => random.Next(2) == 0 ? b : (sbyte)random.Next(-128, 128)).ToArray();
    var apiBytes = oldBytes.Select(b => random.Next(2) == 0 ? b : (sbyte)random.Next(-128, 128)).ToArray();
    var expected = currentBytes.Select((b,i) => b == oldBytes[i] ? apiBytes[i] : b).ToArray();
    st.Run(oldBytes, currentBytes, apiBytes);
    Check(currentBytes.SequenceEqual(expected), "emitted ST arbitrary-size PLC-first merge " + size);
}
Check(NativePreflight.Start("not-a-real-interface", 480, 476, 101, 100, 102, 1, 1) == -1,
    "native bridge rejects unequal image sizes before NIC/network initialization");

using var extendedStream = File.OpenRead(Path.Combine(AppContext.BaseDirectory, "StringsAndArrays.L5X"));
var extended = L5xCatalog.Read(extendedStream);
var extendedBundle = Generator.Generate(extended, [new("Metrics", Direction.PlcToApp), new("Values", Direction.PlcToApp),
    new("Cubes", Direction.PlcToApp), new("Stations", Direction.PlcToApp), new("Request", Direction.AppToPlc)], assemblyBytes: 480);
var extendedConfig = extendedBundle.Configuration;
Check(extendedConfig.PlcToAppBytes == 480 && extendedConfig.AppToPlcBytes == 480, "fixed-size complete round-trip contract");
ExchangeTests.Run(bundle, extendedBundle, Check);
Check(extendedConfig.Fields.Single(f => f.Path == "Metrics.Title").StringCapacity == 82 &&
    extendedConfig.Fields.Single(f => f.Path == "Request.Recipe").StringCapacity == 128, "default and custom STRING capacities retained");
Check(extendedConfig.Fields.Any(f => f.Path == "Values[1,2]") && extendedConfig.Fields.Any(f => f.Path == "Cubes[1,1,1]") &&
    extendedConfig.Fields.Any(f => f.Path == "Stations[1,1].Name"), "2D/3D primitives and UDT arrays use Logix comma subscripts");
Check(extendedConfig.Fields.Any(f => f.Path == "Stations[1,1].History[1]"), "multidimensional UDT arrays retain legal one-dimensional member arrays");
using (var invalidMember = new MemoryStream(Encoding.UTF8.GetBytes("""
<RSLogix5000Content SoftwareRevision="35.03"><Controller Name="Invalid"><DataTypes><DataType Name="Bad"><Members><Member Name="Grid" DataType="DINT" Dimension="2,2"/></Members></DataType></DataTypes><Tags><Tag Name="BadTag" DataType="Bad"/></Tags></Controller></RSLogix5000Content>
""")))
    Check(!L5xCatalog.Read(invalidMember).Tags.Single().Selectable, "unsupported native multidimensional UDT member is rejected");
Check(extendedBundle.ModelCs.Contains("PlcVariable<float>[,] Tag_Values") && extendedBundle.ModelCs.Contains("PlcVariable<long>[,,] Tag_Cubes"), "generated C# rectangular array declarations");
var stringsArray = Generator.Generate(extended, [new("Names", Direction.AppToPlc)]);
Check(stringsArray.ModelCs.Contains("PlcVariable<string>[,]") && stringsArray.Configuration.Fields.All(f => f.StringCapacity == 12), "multidimensional custom STRING array");
Check(extendedConfig.Fields.OrderBy(f => f.Offset).Zip(extendedConfig.Fields.OrderBy(f => f.Offset).Skip(1)).All(pair =>
    pair.First.Offset + pair.First.WireBytes <= pair.Second.Offset), "shared offsets have no overlap across owners");
Throws<ArgumentException>(() => Generator.Generate(extended, [new("Metrics", Direction.PlcToApp)], assemblyBytes: 100), "too-small fixed assembly rejected");
Throws<FormatException>(() => (extendedConfig with { AppToPlcBytes = 476 }).Validate(), "asymmetric images rejected");
Throws<FormatException>(() => (extendedConfig with { SchemaVersion = 1 }).Validate(), "old wire contracts require regeneration");
Check(ArrayShape.Subscript(5, [2,3]) == "1,2" && ArrayShape.Subscript(7, [2,2,2]) == "1,1,1", "array order is rightmost-index fastest");
Throws<ArgumentException>(() => ArrayShape.Count([1,1,1,1]), "rank above three rejected");
using (var reversedString = new MemoryStream(Encoding.UTF8.GetBytes("""
<RSLogix5000Content SoftwareRevision="35.03"><Controller Name="Invalid"><DataTypes><DataType Name="WrongOrder" Family="StringFamily"><Members><Member Name="DATA" DataType="SINT" Dimension="12"/><Member Name="LEN" DataType="DINT" Dimension="0"/></Members></DataType></DataTypes><Tags><Tag Name="BadString" DataType="WrongOrder"/></Tags></Controller></RSLogix5000Content>
""")))
    Check(!L5xCatalog.Read(reversedString).Tags.Single().Selectable, "string definitions must have native LEN-before-DATA layout");
var stringField = new FieldMap("Text", PlcType.STRING, Direction.AppToPlc, 24, StringCapacity: 82);
byte[] nativeString = Enumerable.Repeat((byte)0xCC, 88).ToArray();
string text = "A\0éÿ";
var encodedString = LogixStringBuffer.Encode(stringField, text, nativeString);
Check(LogixStringBuffer.Decode(stringField, encodedString) == text && encodedString[86] == 0xCC && encodedString[87] == 0xCC,
    "counted strings preserve null/8-bit data and native tail padding");
Throws<ArgumentException>(() => LogixStringBuffer.Encode(stringField, new string('A', 83), nativeString), "over-capacity STRING rejected without truncation");
Throws<ArgumentException>(() => LogixStringBuffer.Encode(stringField, "漢", nativeString), "unrepresentable characters rejected without substitution");
var badString = encodedString.ToArray(); BinaryPrimitives.WriteInt32LittleEndian(badString, -1);
Throws<FormatException>(() => LogixStringBuffer.Decode(stringField, badString), "negative native STRING LEN rejected");
BinaryPrimitives.WriteInt32LittleEndian(badString, 83);
Throws<FormatException>(() => LogixStringBuffer.Decode(stringField, badString), "native STRING LEN above capacity rejected");
Throws<FormatException>(() => LogixStringBuffer.Decode(stringField, new byte[84]), "wrong native STRING buffer size rejected");
var extendedModel = new TestModel(extendedConfig);
var recipe = (PlcVariable<string>)extendedModel.Variables.Single(v => v.Map.Path == "Request.Recipe");
var execute = (PlcVariable<bool>)extendedModel.Variables.Single(v => v.Map.Path == "Request.Execute");
recipe.Value = new string('R', 128); execute.Value = true;
Throws<ArgumentException>(() => recipe.Value = new string('R', 129), "typed custom STRING setter enforces declared capacity");
var extendedCodec = new ImplicitImage(extendedConfig, extendedModel);
byte[] plcImage = new byte[480];
int[] extHeader = [ImplicitImage.Magic, ImplicitImage.ProtocolVersion, BinaryPrimitives.ReadInt32LittleEndian(Convert.FromHexString(extendedConfig.SchemaHash)), 1, 0, 1];
for (int i = 0; i < 6; i++) ImplicitImage.WriteInt(plcImage, i * 4, extHeader[i]);
foreach (var variable in extendedModel.Variables) PlcTypes.Encode(variable.Map, variable.Map.Type == PlcType.STRING ? "PLC" : variable.BoxedValue, plcImage.AsSpan(variable.Map.Offset));
extendedCodec.ReceivePlcToApp(plcImage, now);
Check(recipe.Value == new string('R', 128) && recipe.ObservedValue == "PLC", "PLC readback is typed and does not overwrite desired app STRING");
var echoed = extendedCodec.BuildAppToPlc();
var title = extendedConfig.Fields.Single(f => f.Path == "Metrics.Title");
Check(echoed.AsSpan(title.Offset, title.WireBytes).SequenceEqual(plcImage.AsSpan(title.Offset, title.WireBytes)), "PLC-owned bytes round trip unchanged");
Check((string)PlcTypes.Decode(recipe.Map, echoed.AsSpan(recipe.Map.Offset)) == recipe.Value, "app-owned STRING overlays the full received image");
Throws<InvalidOperationException>(() => extendedCodec.BuildAppToPlc(), "pending command cannot change bytes or baseline");
var nextPlc = plcImage.ToArray(); ImplicitImage.WriteInt(nextPlc, 12, 2); ImplicitImage.WriteInt(nextPlc, 16, ImplicitImage.ReadInt(echoed, 12));
PlcTypes.Encode(title, "NEW PLC", nextPlc.AsSpan(title.Offset));
extendedCodec.ReceivePlcToApp(nextPlc, now.AddMilliseconds(1));
Check(recipe.Value == "PLC" && !recipe.Dirty, "implicit merge acknowledgement adopts PLC-winning STRING instead of reasserting rejected edits");
Check(!extendedCodec.ReceivePlcToApp(nextPlc, now.AddMilliseconds(2)), "duplicate frames cannot advance native command acknowledgements");
var thirdOutput = extendedCodec.BuildAppToPlc();
var badImage = plcImage.ToArray(); ImplicitImage.WriteInt(badImage, 16, ImplicitImage.ReadInt(thirdOutput, 12));
PlcTypes.Encode(title, "INVALID IMAGE", badImage.AsSpan(title.Offset));
ImplicitImage.WriteInt(badImage, 12, 3); BinaryPrimitives.WriteInt32LittleEndian(badImage.AsSpan(recipe.Map.Offset), 129);
Throws<FormatException>(() => extendedCodec.ReceivePlcToApp(badImage, now), "malformed implicit STRING rejects entire image");
Check((string)extendedModel.Variables.Single(v => v.Map.Path == title.Path).BoxedValue == "NEW PLC", "late STRING error publishes none of the staged image");
var extRungs = string.Join("\n", XDocument.Parse(extendedBundle.RoutineL5x).Descendants("Text").Select(e => e.Value));
Check(extRungs.Contains("LEQ(LogixRelay_PS_StringLen") && extRungs.Contains("OTE(LogixRelay_PS_FrameValid)") && extRungs.Contains("OTE(LogixRelay_PS_MergedValid)"), "PLC validates incoming and merged STRING lengths before applying fields");
Check(extRungs.Contains("CPS(Request.Recipe,LogixRelay_PS_Tx[") && extRungs.Contains("CPS(Values[1,2],"), "PLC transmits command readbacks and multidimensional elements");
var extTransport = new FakeTransport();
foreach (var variable in extendedModel.Variables) extTransport.Values[variable.Map.Path] = variable.Map.Type == PlcType.STRING ? "EXPLICIT" : variable.BoxedValue;
recipe.Value = "NEW";
await new ExplicitSession(extendedConfig, extendedModel, extTransport).SynchronizeOnceAsync();
Check(Equals(extTransport.Values[recipe.Map.Path], "NEW") && !recipe.Dirty, "explicit custom STRING changes synchronize through field-aware transport");
Check(recipe.ObservedValue == "EXPLICIT", "explicit command readback does not replace desired value");
var longString = stringField with { StringCapacity = 65535 };
var longBuffer = new byte[65540]; var maximumText = new string('L', 65535);
PlcTypes.Encode(longString, maximumText, longBuffer);
Check((string)PlcTypes.Decode(longString, longBuffer) == maximumText, "maximum custom STRING capacity is supported by the codec");
var longCatalog = new TagCatalog("LongString", "35.03", [new("LongText", "LongText", "LargeText", "Read/Write", [], StringCapacity: 65535)]);
Throws<FormatException>(() => Generator.Generate(longCatalog, [new("LongText", Direction.AppToPlc)]), "oversize STRING requires explicit mode or a split protocol");
Check(Generator.Generate(longCatalog, [new("LongText", Direction.AppToPlc)], includeImplicit: false).Configuration.AppToPlcBytes > 65535,
    "explicit-only generation supports custom strings beyond one I/O assembly");
byte[] syntheticModule = Encoding.UTF8.GetBytes("""
<RSLogix5000Content SchemaRevision="1.0" SoftwareRevision="35.03" TargetName="Template" TargetType="Module"><Controller Name="SyntheticContext"><Modules>
<Module Use="Target" Name="Template" CatalogNumber="ETHERNET-MODULE" ParentModule="Local" ParentModPortId="2"><EKey State="Disabled"/><Ports><Port Id="2" Address="192.0.2.2" Type="Ethernet"/></Ports>
<Communications CommMethod="native-mode-sentinel" PrimCxnInputSize="480" PrimCxnOutputSize="480"><ConfigTag ConfigSize="0"><Data Format="L5K"><![CDATA[[5,102,[0,0]]]]></Data></ConfigTag>
<Connections><Connection Name="Standard" RPI="10000" Type="Output" InputCxnPoint="101" OutputCxnPoint="100" InputSize="480" OutputSize="480">
<InputTag><Data Format="Decorated"><Structure DataType="AB:ETHERNET_MODULE_SINT_480Bytes:I:0"><ArrayMember Name="Data" DataType="SINT" Dimensions="480"/></Structure></Data></InputTag>
<OutputTag><Data Format="L5K"><![CDATA[[[0]]]]></Data><Data Format="Decorated"><Structure DataType="AB:ETHERNET_MODULE_SINT_480Bytes:O:0"><ArrayMember Name="Data" DataType="SINT" Dimensions="480"/></Structure></Data></OutputTag>
</Connection></Connections></Communications></Module></Modules></Controller></RSLogix5000Content>
""");
var moduleInfo = ModuleTemplate.Inspect(syntheticModule);
Check(moduleInfo.Errors.Count == 0 && moduleInfo.ConfigurationAssembly == 102, "module template structural inspection (synthetic fixture, not native evidence)");
var generatedModule = ModuleTemplate.Generate(syntheticModule, bundle.Configuration, "RelayTest", "192.0.2.10");
var moduleDoc = XDocument.Parse(generatedModule.Xml);
Check(moduleDoc.Descendants("Communications").Single().Attribute("CommMethod")!.Value == "native-mode-sentinel" &&
    moduleDoc.Descendants("ConfigTag").Single().Element("Data")!.Value == "[5,102,[0,0]]", "module generation preserves opaque native mode and configuration prefix");
Check(moduleDoc.Descendants("ArrayMember").All(a => (string?)a.Attribute("Dimensions") == "60" && a.Elements("Element").Count() == 60), "module data images and decorated elements resize together");
Check(moduleDoc.Descendants("Connection").Single().Attribute("RPI")!.Value == "20000" && moduleDoc.Root!.Attribute("TargetName")!.Value == "RelayTest", "module RPI, name, and PC address are emitted from settings");
var mismatchedModule = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(syntheticModule).Replace("OutputCxnPoint=\"100\"", "OutputCxnPoint=\"150\""));
Throws<ArgumentException>(() => ModuleTemplate.Generate(mismatchedModule, bundle.Configuration, "RelayTest", "192.0.2.10"), "unmatched native assembly instances require a new export");
var statusModule = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(syntheticModule).Replace("</Connections>", "<Connection Name=\"Status\" /></Connections>"));
Throws<ArgumentException>(() => ModuleTemplate.Generate(statusModule, bundle.Configuration, "RelayTest", "192.0.2.10"), "unsupported separate status connection is not silently removed");

if (args.Length == 2 && args[0] == "--generate")
{
    Directory.CreateDirectory(args[1]);
    await File.WriteAllTextAsync(Path.Combine(args[1], "GeneratedTags.g.cs"), bundle.ModelCs, new UTF8Encoding(false));
    await File.WriteAllTextAsync(Path.Combine(args[1], "logixrelay.json"), bundle.Configuration.ToJson(), new UTF8Encoding(false));
    await File.WriteAllTextAsync(Path.Combine(args[1], "RelayRoutine.L5X"), bundle.RoutineL5x, new UTF8Encoding(false));
    await File.WriteAllTextAsync(Path.Combine(args[1], "ModuleSetup.txt"), bundle.ModuleInstructions, new UTF8Encoding(false));
}
if (args.Length == 2 && args[0] == "--export-aoi")
{
    Directory.CreateDirectory(args[1]);
    await File.WriteAllTextAsync(Path.Combine(args[1], "LTR_SintMerge.L5X"), SintMergeAoi.GenerateL5x(), new UTF8Encoding(false));
}
if (args.Length == 2 && args[0] == "--generate-types")
{
    var typed = TypesAndEventsTests.Bundle(); Directory.CreateDirectory(args[1]);
    await File.WriteAllTextAsync(Path.Combine(args[1],"GeneratedTags.g.cs"),typed.ModelCs,new UTF8Encoding(false));
    await File.WriteAllTextAsync(Path.Combine(args[1],"logixrelay.json"),typed.Configuration.ToJson(),new UTF8Encoding(false));
    await File.WriteAllTextAsync(Path.Combine(args[1],"RelayRoutine.L5X"),typed.RoutineL5x,new UTF8Encoding(false));
}
if (args.Length == 2 && args[0] == "--generate-bidirectional")
{
    var shared = BidirectionalTests.Bundle(); Directory.CreateDirectory(args[1]);
    await File.WriteAllTextAsync(Path.Combine(args[1],"GeneratedTags.g.cs"),shared.ModelCs,new UTF8Encoding(false));
    await File.WriteAllTextAsync(Path.Combine(args[1],"logixrelay.json"),shared.Configuration.ToJson(),new UTF8Encoding(false));
    await File.WriteAllTextAsync(Path.Combine(args[1],"RelayRoutine.L5X"),shared.RoutineL5x,new UTF8Encoding(false));
}
if (args.Length == 2 && args[0] == "--generate-emulation")
{
    using var motionStream = File.OpenRead(Path.Combine(AppContext.BaseDirectory, "Motion.L5X"));
    var motionCatalog = L5xCatalog.Read(motionStream);
    var motionSelections = new TagSelection[] { new("Motion.CommandPosition", Direction.AppToPlc), new("Motion.ActualPosition", Direction.PlcToApp),
        new("Motion.InPosition", Direction.PlcToApp), new("Motion.Speed", Direction.PlcToApp) };
    var motion = Generator.Generate(motionCatalog, motionSelections);
    var motionTriggered = Generator.Generate(motionCatalog, motionSelections, explicitOptions: new(TriggerTag: "RequestSequence", HandledTriggerTag: "HandledSequence", ResponseTag: "ResponseSequence"));
    Directory.CreateDirectory(args[1]);
    await File.WriteAllTextAsync(Path.Combine(args[1], "GeneratedTags.g.cs"), motion.ModelCs, new UTF8Encoding(false));
    await File.WriteAllTextAsync(Path.Combine(args[1], "logixrelay.json"), motion.Configuration.ToJson(), new UTF8Encoding(false));
    await File.WriteAllTextAsync(Path.Combine(args[1], "logixrelay-triggered.json"), motionTriggered.Configuration.ToJson(), new UTF8Encoding(false));
}
if (args.Length == 2 && args[0] == "--generate-extended")
{
    Directory.CreateDirectory(args[1]);
    await File.WriteAllTextAsync(Path.Combine(args[1], "GeneratedTags.g.cs"), extendedBundle.ModelCs, new UTF8Encoding(false));
    await File.WriteAllTextAsync(Path.Combine(args[1], "logixrelay.json"), extendedConfig.ToJson(), new UTF8Encoding(false));
    await File.WriteAllTextAsync(Path.Combine(args[1], "RelayRoutine.L5X"), extendedBundle.RoutineL5x, new UTF8Encoding(false));
    await File.WriteAllTextAsync(Path.Combine(args[1], "ModuleSetup.txt"), extendedBundle.ModuleInstructions, new UTF8Encoding(false));
}
Console.WriteLine($"{passed} checks passed. Runtime: {Environment.Version}");

sealed class TestModel : IPlcModel
{
    public IReadOnlyList<IPlcVariable> Variables { get; }
    public TestModel(SyncConfiguration config) => Variables = config.Fields.Select(f => (IPlcVariable)(f.Type switch
    {
        PlcType.BOOL => (IPlcVariable)new PlcVariable<bool>(f), PlcType.SINT => new PlcVariable<sbyte>(f), PlcType.INT => new PlcVariable<short>(f),
        PlcType.DINT => new PlcVariable<int>(f), PlcType.LINT => new PlcVariable<long>(f), PlcType.REAL => new PlcVariable<float>(f), PlcType.STRING => new PlcVariable<string>(f),
        PlcType.USINT => new PlcVariable<byte>(f), PlcType.UINT => new PlcVariable<ushort>(f), PlcType.UDINT => new PlcVariable<uint>(f), PlcType.ULINT => new PlcVariable<ulong>(f),
        PlcType.DT => new PlcVariable<PlcDateTime>(f), PlcType.TIME => new PlcVariable<PlcTime>(f), _ => throw new Exception()
    })).ToArray();
}
sealed class FakeTransport : IExplicitTransport
{
    public Dictionary<string, object> Values { get; } = [];
    public Action? DuringWrite { get; set; }
    public Action? OnAcknowledged { get; set; }
    public bool FailWrites { get; set; }
    public HashSet<string> FailPaths { get; } = [];
    public List<(string Path, object Value)> Writes { get; } = [];
    public ValueTask<object> ReadAsync(string path, PlcType type, CancellationToken cancellationToken) => ValueTask.FromResult(Values[path]);
    public ValueTask WriteAsync(string path, PlcType type, object value, CancellationToken cancellationToken)
    {
        if (FailWrites || FailPaths.Contains(path)) throw new IOException("Injected failure");
        Writes.Add((path, value));
        if (path == "Command.Setpoint") DuringWrite?.Invoke();
        Values[path] = value;
        if (path == "AckSequence") OnAcknowledged?.Invoke();
        return ValueTask.CompletedTask;
    }
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
static class NativePreflight
{
    [System.Runtime.InteropServices.DllImport("LogixTagRelayOpener", EntryPoint = "ltr_start", CallingConvention = System.Runtime.InteropServices.CallingConvention.Cdecl, CharSet = System.Runtime.InteropServices.CharSet.Ansi)]
    internal static extern int Start(string nic, int inputBytes, int outputBytes, int inputAssembly, int outputAssembly, int configAssembly, uint serial, ushort seed);
}
