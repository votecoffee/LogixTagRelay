using System.Reflection;
using System.Runtime.InteropServices;
using Consumer.Contracts;
using LogixTagRelay;
using LogixTagRelay.Design;
using LogixTagRelay.Emulation;
using LogixTagRelay.Logix;
using LogixTagRelay.Opener;

int checks = 0;
void Check(bool condition, string description)
{
    if (!condition) throw new InvalidOperationException(description);
    checks++; Console.WriteLine("PASS " + description);
}

string expectedVersion = args[0], expectedCommit = args[1];
var config = ReleaseTags.CreateConfiguration();
var tags = new ReleaseTags();
Check(typeof(PlcVariable<>).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion == expectedVersion + "+" + expectedCommit, "restored assembly version and commit");
Check(File.Exists(Path.Combine(AppContext.BaseDirectory, "contracts/contract.ltrx/logixrelay.json")), "generated JSON propagated through the contract project reference");
Check(config.Heartbeat is { Tag: "ApiAlive", IntervalMs: 5000 } && config.Fields.All(f => f.Path != "ApiAlive"), "explicit-only five-second heartbeat outside the implicit payload");
Check(config.PlcToAppBytes == config.AppToPlcBytes && config.PlcToAppBytes <= 480, "single symmetric bounded implicit image");
Check(SintMergeAoi.StructuredText.Contains("FOR", StringComparison.Ordinal) && ContractComparison.Compare(config, config).Count == 0, "Design resources and contract comparison loaded from NuGet");
Check(LogixAddress.SymbolicPath(config.Fields[0]) == config.Fields[0].Path, "explicit driver address helper loaded");
await using (var unusedDriver = new LogixTransport("192.0.2.1")) { } // Construction and disposal do not contact the PLC.

var memory = new PlcMemory(config);
memory.Set("Machine.Actual", 1.5f);
memory.Set("Machine.Label", "initial");
memory.Set("Utc", new PlcDateTime(123456789));
await using (var transport = new RecordingTransport(memory))
{
    var session = new ExplicitSession(config, tags, transport);
    int transitions = 0;
    tags.TagValueChanged += (_, _) => transitions++;
    await session.SynchronizeOnceAsync();
    Check(tags.Tag_Utc.Value.UnixMicroseconds == 123456789 && tags.Tag_Grid.GetLength(0) == 2 && tags.Tag_Grid.GetLength(1) == 2, "exact PLC timestamp and rectangular array contract types");
    int before = transitions;
    await session.SynchronizeOnceAsync();
    Check(transitions == before && tags.Tag_Machine.Tag_Actual.Value == 1.5f, "change-only notifications and typed UDT reads");
    memory.Set("Machine.Actual", 7.5f);
    tags.Tag_Machine.Tag_Command.Value = 42;
    tags.Tag_Machine.Tag_Label.Value = "package";
    tags.Tag_Grid[1, 1].Value = byte.MaxValue;
    await session.SynchronizeOnceAsync();
    Check(transport.Writes.SequenceEqual(new[] { "Machine.Command", "Machine.Label", "Grid[1,1]" }), "explicit writes address only changed UDT and array members");
    Check((float)memory.Read("Machine.Actual") == 7.5f && (int)memory.Read("Machine.Command") == 42 && (string)memory.Read("Machine.Label") == "package", "PLC edits, custom STRING and API command round trip");
    await session.HeartbeatOnceAsync();
    await session.HeartbeatOnceAsync();
    Check((bool)memory.Read("ApiAlive") && session.Diagnostics.Snapshot.HeartbeatTransitions == 1, "explicit heartbeat seeds and toggles the separate BOOL");
}

var implicitTags = new ReleaseTags();
implicitTags.Tag_Machine.Tag_Command.Value = 84;
var plc = new ImplicitPlc(memory) { ApplyCommands = true };
var implicitSession = new ImplicitContractSession(config, implicitTags,
    _ => ValueTask.FromResult<IImplicitTransport>(new MemoryImplicitTransport(plc)),
    heartbeatConnect: _ => ValueTask.FromResult<IExplicitTransport>(new MemoryExplicitTransport(memory)));
using (var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
{
    var run = implicitSession.RunAsync(stop.Token);
    try
    {
        while ((int)memory.Read("Machine.Command") != 84 || implicitSession.HeartbeatDiagnostics?.Snapshot.LastCommunication is null)
        {
            if (run.IsCompleted) await run;
            await Task.Delay(10, stop.Token);
        }
    }
    finally
    {
        stop.Cancel();
        try { await run; } catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
    }
}
Check((int)memory.Read("Machine.Command") == 84 && implicitSession.Diagnostics.Snapshot.SuccessfulUpdates > 0, "managed implicit recovery and baseline handshake through packaged memory transports");
Check(implicitSession.HeartbeatDiagnostics?.Snapshot.LastCommunication is not null, "implicit-only hosting provisions a separate explicit heartbeat");
var script = new ScriptRunner(memory);
script.Configure("plc.set('Machine.Actual', plc.get('Machine.Command')); plc.set('Machine.InPosition', true);");
Check(script.Step() && (float)memory.Read("Machine.Actual") == 84f && (bool)memory.Read("Machine.InPosition"), "packaged JavaScript engine executes a PLC-side response");
var fault = new PlcTagReadException("Machine.Actual", "192.0.2.1", "1,0", new TimeoutException(), PlcFailureKind.Timeout);
Check(fault.TagPath == "Machine.Actual" && fault.Operation == PlcOperation.ReadTag && fault.IsTransient, "typed fault metadata available to consumers");
Check(OperatingSystem.IsWindows() && RuntimeInformation.ProcessArchitecture == Architecture.X64, "Windows x64 native deployment target");
Check(OpenerAdapter.NativeAbiVersion == 2, "packaged native DLL loads with ABI 2");
using (var probeTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(15)))
    Check(await OpenerAdapter.ProbeWorkerAsync(probeTimeout.Token) == 2, "published application starts and stops its owned native worker without network listeners");
Console.WriteLine($"PASS {checks} package-consumer checks on {RuntimeInformation.FrameworkDescription}; no PLC contacted.");

sealed class RecordingTransport(PlcMemory memory) : IExplicitTransport
{
    private readonly MemoryExplicitTransport inner = new(memory);
    public List<string> Writes { get; } = [];
    public ValueTask<object> ReadAsync(string path, PlcType type, CancellationToken cancellationToken) => inner.ReadAsync(path, type, cancellationToken);
    public ValueTask WriteAsync(string path, PlcType type, object value, CancellationToken cancellationToken) { Writes.Add(path); return inner.WriteAsync(path, type, value, cancellationToken); }
    public ValueTask<object> ReadAsync(FieldMap field, CancellationToken cancellationToken) => inner.ReadAsync(field, cancellationToken);
    public ValueTask WriteAsync(FieldMap field, object value, CancellationToken cancellationToken) { Writes.Add(field.Path); return inner.WriteAsync(field, value, cancellationToken); }
    public ValueTask DisposeAsync() => inner.DisposeAsync();
}
