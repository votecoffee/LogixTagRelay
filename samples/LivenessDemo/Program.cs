using Example.Contracts.Liveness;
using LogixTagRelay;
using LogixTagRelay.Emulation;

var config = LivenessTags.CreateConfiguration();
bool quick = args.Contains("--quick");
if (quick) config = config with { Heartbeat = config.Heartbeat! with { IntervalMs = 20 } };
var model = new LivenessTags(); var memory = new PlcMemory(config); var plc = new ImplicitPlc(memory);
var session = new ImplicitContractSession(config, model,
    _ => ValueTask.FromResult<IImplicitTransport>(new MemoryImplicitTransport(plc)),
    new(InitialDelayMs: 1000, DelayMode: ReconnectDelayMode.Fixed),
    _ => ValueTask.FromResult<IExplicitTransport>(new MemoryExplicitTransport(memory)));
long lastEdges = 0;
session.HeartbeatDiagnostics!.Changed += (_, state) =>
{
    if (state.HeartbeatTransitions <= lastEdges) return;
    lastEdges = state.HeartbeatTransitions;
    Console.WriteLine($"Explicit heartbeat: {state.HeartbeatValue}, edges={state.HeartbeatTransitions}");
};
using var stop = new CancellationTokenSource(quick ? 250 : 11200);
await session.RunAsync(stop.Token);
if (session.HeartbeatDiagnostics.Snapshot.HeartbeatTransitions < 2 || config.Fields.Any(f => f.Path == "ApiAlive")) throw new Exception("Heartbeat did not run through the explicit channel.");
Console.WriteLine($"One implicit contract, {config.PlcToAppBytes}-byte images; independent explicit heartbeat every {config.Heartbeat!.IntervalMs} ms. No heartbeat bytes in the implicit image.");
