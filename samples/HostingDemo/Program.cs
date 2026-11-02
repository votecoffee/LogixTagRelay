using Example.Contracts.Fast;
using Example.Contracts.Slow;
using Example.Contracts.Io;
using LogixTagRelay;
using LogixTagRelay.Emulation;

SyncConfiguration Load(string name) => SyncConfiguration.FromJson(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "contracts", name + ".ltrx", "logixrelay.json")));
var fastConfig = Load("fast"); var slowConfig = Load("slow"); var ioConfig = Load("io");
var fast = new FastTags(); var slow = new SlowTags(); var io = new IoTags();
var fastMemory = new PlcMemory(fastConfig); var slowMemory = new PlcMemory(slowConfig); var ioMemory = new PlcMemory(ioConfig);
fastMemory.Set("Telemetry.Count16", (ushort)99); slowMemory.Set("Telemetry.Count64", ulong.MaxValue);
ioMemory.Set("Telemetry.Elapsed", PlcTime.FromTimeSpan(TimeSpan.FromSeconds(1.5)));
var host = new RelayHost();
host.AddExplicit("fast", fastConfig, fast, _ => ValueTask.FromResult<IExplicitTransport>(new MemoryExplicitTransport(fastMemory)));
host.AddExplicit("slow", slowConfig, slow, _ => ValueTask.FromResult<IExplicitTransport>(new MemoryExplicitTransport(slowMemory)));
var plc = new ImplicitPlc(ioMemory);
host.Add("io", new ImplicitContractSession(ioConfig, io, _ => ValueTask.FromResult<IImplicitTransport>(new MemoryImplicitTransport(plc))));
using var stop = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
fast.Tag_Telemetry_002ECode.Value = 42; slow.Tag_Telemetry_002ECount32.Value = 123U;
await host.RunAsync(stop.Token);
if ((byte)fastMemory.Read("Telemetry.Code") != 42 || (uint)slowMemory.Read("Telemetry.Count32") != 123U || io.Tag_Telemetry_002EElapsed.Value != new PlcTime(1500000)) throw new Exception("Hosted synchronization failed.");
foreach (var (name, session) in host.Sessions)
{
    var status = session.Diagnostics.Snapshot;
    if (status.SuccessfulUpdates < 1 || status.Failures != 0) throw new Exception("Hosted group did not synchronize.");
    Console.WriteLine($"{name}: {session.Kind}, updates={status.SuccessfulUpdates}, last cycle={status.LastCycleDuration.TotalMilliseconds:F2} ms, state={status.State}");
}
Console.WriteLine($"Two independent explicit contracts plus one {ioConfig.PlcToAppBytes}-byte implicit image passed.");
