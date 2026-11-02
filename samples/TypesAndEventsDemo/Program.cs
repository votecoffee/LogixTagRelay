using System.ComponentModel;
using LogixTagRelay;
using LogixTagRelay.Emulation;
using LogixTagRelay.Generated;

var config = SyncConfiguration.FromJson(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "logixrelay.json")));
var tags = new GeneratedTags();
var memory = new PlcMemory(config);
memory.Set("Telemetry.Code", (byte)255);
memory.Set("Telemetry.Count16", ushort.MaxValue);
memory.Set("Telemetry.Count32", uint.MaxValue);
memory.Set("Telemetry.Count64", ulong.MaxValue);
memory.Set("Telemetry.StartedAt", PlcDateTime.FromDateTimeOffset(new(2026, 11, 2, 12, 0, 0, TimeSpan.Zero)));
memory.Set("Telemetry.Elapsed", PlcTime.FromTimeSpan(TimeSpan.FromSeconds(1.25)));
int changes = 0;
tags.TagValueChanged += (_, e) =>
{
    changes++;
    Console.WriteLine($"{e.Origin}: {e.Path}: {e.OldValue} -> {e.NewValue}");
};
tags.Tag_Grid[1, 1].PropertyChanged += (_, e) =>
{
    if (e.PropertyName == nameof(PlcVariable<uint>.Value)) Console.WriteLine("Array element Value changed");
};
tags.NotificationFailed += (_, e) => Console.Error.WriteLine(e.Exception.Message);
if (tags is not INotifyPropertyChanged) throw new Exception("Generated contract must implement property notifications.");
await using var transport = new MemoryExplicitTransport(memory);
var explicitSession = new ExplicitSession(config, tags, transport);
var codec = new ImplicitImage(config, tags); var plc = new ImplicitPlc(memory);
bool implicitMode = args.Contains("--implicit");
if (implicitMode) codec.ReceivePlcToApp(plc.Output(), DateTimeOffset.UtcNow);
async Task Cycle()
{
    if (implicitMode) codec.ReceivePlcToApp(plc.Exchange(codec.BuildAppToPlc()), DateTimeOffset.UtcNow);
    else await explicitSession.SynchronizeOnceAsync();
}
await Cycle(); int initial = changes;
await Cycle(); await Cycle();
if (changes != initial) throw new Exception("Identical reads raised a value event.");
tags.Tag_Telemetry.Tag_Code.Value = 42;
tags.Tag_Grid[1, 1].Value = uint.MaxValue;
await Cycle();
if ((byte)memory.Read("Telemetry.Code") != 42 || (uint)memory.Read("Grid[1,1]") != uint.MaxValue) throw new Exception("Typed writes failed.");
memory.Set("Telemetry.Count32", 123U); await Cycle();
if (tags.Tag_Telemetry.Tag_Count32.Value != 123U) throw new Exception("PLC change was not observed.");
Console.WriteLine($"{(implicitMode ? "Implicit" : "Explicit")}: {changes} value events; identical cycles stayed silent. DT UTC={tags.Tag_Telemetry.Tag_StartedAt.Value.ToDateTimeOffset():O}, TIME={tags.Tag_Telemetry.Tag_Elapsed.Value.ToTimeSpan()}.");
