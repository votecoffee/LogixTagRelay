using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using LogixTagRelay;
using LogixTagRelay.Emulation;

// Owns an ephemeral loopback host and exits after exercising the actual HTTP routes/client.
sealed class EmulatorSelfTest(EmulatorWorkspace workspace, IHostApplicationLifetime lifetime, IServer server) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = lifetime.ApplicationStarted.Register(() => started.TrySetResult());
        await started.Task.WaitAsync(stoppingToken);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        var ct = timeout.Token;
        int count = 0;
        void Check(bool value, string name) { if (!value) throw new Exception(name); Console.WriteLine("PASS HTTP " + name); count++; }
        try
        {
            var uri = new Uri(server.Features.Get<IServerAddressesFeature>()!.Addresses.Single());
            using (var pageClient = new HttpClient()) Check((await pageClient.GetStringAsync(uri, ct)).Contains("PLC memory emulator"), "published/static GUI is served independently of the working directory");
            var session = workspace.Current; var config = session.Memory.Configuration; string script = session.Scripts.Status.Source;
            var model = new Model(config);
            model.Set("Motion.CommandPosition", 3f);
            await using var client = await EmulatorClient.ConnectAsync(config, uri, ct);
            var explicitSession = new ExplicitSession(config, model, client);
            await explicitSession.SynchronizeOnceAsync(ct); session.Scripts.Step(1); await explicitSession.SynchronizeOnceAsync(ct);
            Check((float)model.Get("Motion.ActualPosition") == 3f && (bool)model.Get("Motion.InPosition"), "explicit commands drive scripted motion through shared HTTP memory");
            Check(session.Reads > 0 && session.Writes > 0 && session.Memory.Snapshot().Tags.Single(t => t.Map.Path == "Motion.CommandPosition").LastApiValue == "3", "GUI state exposes actual explicit traffic and API requests");
            model.Set("Motion.CommandPosition", 6f); session.Scripts.Configure(script, start: true);
            using (var run = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                var loop = client.RunImplicitAsync(model, run.Token);
                try { await WaitUntil(() => !model.Variables[0].Dirty && Math.Abs((float)model.Get("Motion.ActualPosition") - 6f) < 0.01f && (bool)model.Get("Motion.InPosition"), loop, ct); }
                finally { run.Cancel(); await Canceled(loop, run.Token); }
            }
            Check(session.Implicit!.Status.Accepted > 0 && (float)session.Memory.Read("Motion.ActualPosition") == 6f, "implicit codec exchanges drive scripted motion without native networking");
            session.Scripts.Stop();
            using var http = new HttpClient { BaseAddress = uri };
            using (var rejected = await http.PostAsJsonAsync("api/edit", new { instanceId = session.Contract.InstanceId, path = "Motion.Speed", value = "20" }, ct))
                Check(rejected.StatusCode == HttpStatusCode.Forbidden, "write routes reject requests without the emulator header");
            http.DefaultRequestHeaders.Add("X-LogixTagRelay", "emulator");
            using (var edit = await http.PostAsJsonAsync("api/edit", new { instanceId = session.Contract.InstanceId, path = "Motion.Speed", value = "20" }, ct)) edit.EnsureSuccessStatusCode();
            Check((float)session.Memory.Read("Motion.Speed") == 20f, "GUI edit route changes PLC-side values");
            using (var scriptReply = await http.PostAsJsonAsync("api/script", new { instanceId = session.Contract.InstanceId, action = "step", source = "plc.set('Motion.Speed', 999); throw new Error('expected');", intervalMs = 100 }, ct)) scriptReply.EnsureSuccessStatusCode();
            Check(session.Scripts.Status.Error!.Contains("expected") && (float)session.Memory.Read("Motion.Speed") == 20f, "GUI script errors stop and preserve memory");

            var triggered = config with { Explicit = new(TriggerTag: "RequestSequence", HandledTriggerTag: "HandledSequence", ResponseTag: "ResponseSequence") };
            workspace.Load(triggered.ToJson()); session = workspace.Current;
            session.Memory.Set("Motion.Speed", 100f); session.Scripts.Configure(script, start: true);
            var triggeredModel = new Model(triggered); triggeredModel.Set("Motion.CommandPosition", 8f);
            await using (var triggeredClient = await EmulatorClient.ConnectAsync(triggered, uri, ct))
            using (var run = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                var loop = new ExplicitSession(triggered, triggeredModel, triggeredClient).RunAsync(run.Token);
                try { await WaitUntil(() => !triggeredModel.Variables[0].Dirty && (float)triggeredModel.Get("Motion.ActualPosition") == 8f && (bool)triggeredModel.Get("Motion.InPosition"), loop, ct); }
                finally { run.Cancel(); await Canceled(loop, run.Token); }
            }
            Check((int)session.Memory.Read("HandledSequence") > 0 && (int)session.Memory.Read("ResponseSequence") > 0, "DINT-triggered HTTP session mirrors handled and ACK values");
            session.Scripts.Stop();
            try { await client.ReadAsync("Motion.ActualPosition", PlcType.REAL, ct); throw new Exception("Stale client was accepted"); }
            catch (IOException) { Check(true, "replaced contract invalidates existing HTTP clients"); }
            try { await using var wrong = await EmulatorClient.ConnectAsync(config, uri, ct); throw new Exception("Wrong contract accepted"); }
            catch (ArgumentException) { Check(true, "client rejects differing trigger/control contracts"); }

            List<FieldMap> fields = [new("Recipe", PlcType.STRING, Direction.AppToPlc, 24, StringCapacity: 12), new("Count", PlcType.LINT, Direction.PlcToApp, 40), new("Grid[1,2]", PlcType.REAL, Direction.PlcToApp, 48)];
            var extended = new SyncConfiguration(SyncConfiguration.ComputeHash(fields), fields, 52, 52, Explicit: new(), Implicit: new());
            workspace.Load(extended.ToJson()); session = workspace.Current;
            session.Memory.SetText("Count", "9007199254740993"); session.Memory.Set("Grid[1,2]", 12.5f);
            var extendedModel = new Model(extended); extendedModel.Set("Recipe", "é\0Recipe");
            await using var extendedClient = await EmulatorClient.ConnectAsync(extended, uri, ct);
            await new ExplicitSession(extended, extendedModel, extendedClient).SynchronizeOnceAsync(ct);
            Check((long)extendedModel.Get("Count") == 9007199254740993L && (float)extendedModel.Get("Grid[1,2]") == 12.5f && (string)session.Memory.Read("Recipe") == "é\0Recipe", "HTTP explicit preserves LINT, counted strings and multidimensional paths");
            var codec = new ImplicitImage(extended, extendedModel);
            codec.ReceivePlcToApp(await extendedClient.ExchangeImplicitAsync(codec.BuildBootstrap(), ct), DateTimeOffset.UtcNow);
            extendedModel.Set("Recipe", "Next");
            codec.ReceivePlcToApp(await extendedClient.ExchangeImplicitAsync(codec.BuildAppToPlc(), ct), DateTimeOffset.UtcNow);
            Check(!extendedModel.Variables[0].Dirty && (string)session.Memory.Read("Recipe") == "Next" && (long)extendedModel.Get("Count") == 9007199254740993L, "HTTP implicit round trip retains typed string and LINT values");
            using (var restore = await http.PostAsJsonAsync("api/restore", new { instanceId = session.Contract.InstanceId, fingerprint = session.Contract.Fingerprint, values = new Dictionary<string,string> { ["Count"] = "7", ["Recipe"] = new('X',13) } }, ct))
                Check(restore.StatusCode == HttpStatusCode.BadRequest && (long)session.Memory.Read("Count") == 9007199254740993L, "invalid memory restore applies no partial changes");
            using (var restore = await http.PostAsJsonAsync("api/restore", new { instanceId = session.Contract.InstanceId, fingerprint = session.Contract.Fingerprint, values = new Dictionary<string,string> { ["Count"] = "7", ["Recipe"] = "Saved" } }, ct)) restore.EnsureSuccessStatusCode();
            Check((long)session.Memory.Read("Count") == 7L && (string)session.Memory.Read("Recipe") == "Saved" && !session.Scripts.Status.Running, "valid memory restore commits the batch and stops scripting");
            List<FieldMap> sharedFields = [new("Shared.Setpoint",PlcType.REAL,Direction.Bidirectional,24),new("Shared.Counts[2]",PlcType.DINT,Direction.Bidirectional,28),new("Shared.Actual",PlcType.REAL,Direction.PlcToApp,32,"Read Only")];
            var sharedConfig = new SyncConfiguration(SyncConfiguration.ComputeHash(sharedFields),sharedFields,36,36,
                Explicit:new(ReadGroups:[new("Shared","SharedData",sharedFields.Select(f => f.Path).ToList())]),Implicit:new());
            workspace.Load(sharedConfig.ToJson()); session = workspace.Current; session.Memory.Set("Shared.Setpoint",10f);
            var sharedModel = new Model(sharedConfig);
            await using var sharedClient = await EmulatorClient.ConnectAsync(sharedConfig,uri,ct);
            var sharedSession = new ExplicitSession(sharedConfig,sharedModel,sharedClient); await sharedSession.SynchronizeOnceAsync(ct);
            Check((float)sharedModel.Get("Shared.Setpoint") == 10f && session.Writes == 0, "bidirectional HTTP UDT batch refreshes clean values without writing defaults");
            sharedModel.Set("Shared.Setpoint",25f); session.Memory.Set("Shared.Counts[2]",99); await sharedSession.SynchronizeOnceAsync(ct);
            Check(session.Writes == 1 && (float)session.Memory.Read("Shared.Setpoint") == 25f && (int)sharedModel.Get("Shared.Counts[2]") == 99,
                "bidirectional HTTP UDT read writes only the edited member and retains independent PLC edits");
            sharedModel.Set("Shared.Setpoint",25f); await sharedSession.SynchronizeOnceAsync(ct);
            Check(session.Writes == 1 && session.Reads == 3, "unchanged bidirectional HTTP cycles read snapshots without redundant writes");
            List<FieldMap> typedFields = [new("Code",PlcType.USINT,Direction.Bidirectional,24),new("Count16",PlcType.UINT,Direction.Bidirectional,28),
                new("Count32",PlcType.UDINT,Direction.Bidirectional,32),new("Count64",PlcType.ULINT,Direction.Bidirectional,36),
                new("StartedAt",PlcType.DT,Direction.Bidirectional,44),new("Elapsed",PlcType.TIME,Direction.Bidirectional,52)];
            var typedConfig = new SyncConfiguration(SyncConfiguration.ComputeHash(typedFields),typedFields,60,60,Explicit:new(),Implicit:new());
            workspace.Load(typedConfig.ToJson()); session = workspace.Current;
            object[] typedValues = [(byte)255,ushort.MaxValue,uint.MaxValue,ulong.MaxValue,new PlcDateTime(1793620800123456),new PlcTime(-9007199254740993)];
            for (int i=0;i<typedFields.Count;i++) session.Memory.Set(typedFields[i].Path,typedValues[i]);
            var typedModel = new Model(typedConfig); int typedChanges = 0;
            foreach (IObservablePlcVariable variable in typedModel.Variables) variable.ValueChanged += (_,_) => typedChanges++;
            await using var typedClient = await EmulatorClient.ConnectAsync(typedConfig,uri,ct);
            var typedSession = new ExplicitSession(typedConfig,typedModel,typedClient); await typedSession.SynchronizeOnceAsync(ct);
            Check(typedFields.Select((f,i) => Equals(typedModel.Get(f.Path),typedValues[i])).All(x => x) && typedChanges == 6, "HTTP explicit reads unsigned and temporal values without precision loss");
            await typedSession.SynchronizeOnceAsync(ct); await typedSession.SynchronizeOnceAsync(ct);
            Check(typedChanges == 6, "unchanged HTTP read cycles do not emit tag value events");
            typedModel.Set("Count64",ulong.MaxValue-1); typedModel.Set("Elapsed",new PlcTime(1234567)); await typedSession.SynchronizeOnceAsync(ct);
            Check((ulong)session.Memory.Read("Count64") == ulong.MaxValue-1 && (PlcTime)session.Memory.Read("Elapsed") == new PlcTime(1234567), "HTTP explicit dirty writes preserve ULINT and TIME");
            var typedCodec = new ImplicitImage(typedConfig,typedModel);
            typedCodec.ReceivePlcToApp(await typedClient.ExchangeImplicitAsync(typedCodec.BuildBootstrap(),ct),DateTimeOffset.UtcNow);
            typedModel.Set("StartedAt",new PlcDateTime(1793620800654321)); typedModel.Set("Code",(byte)200);
            typedCodec.ReceivePlcToApp(await typedClient.ExchangeImplicitAsync(typedCodec.BuildAppToPlc(),ct),DateTimeOffset.UtcNow);
            Check((PlcDateTime)session.Memory.Read("StartedAt") == new PlcDateTime(1793620800654321) && (byte)session.Memory.Read("Code") == 200 && typedModel.Variables.All(v => !v.Dirty), "HTTP implicit merges DT and USINT and acknowledges the exact typed values");
            Console.WriteLine($"{count} emulator HTTP checks passed.");
        }
        catch (Exception failure)
        {
            Console.Error.WriteLine(failure);
            Console.Error.WriteLine(System.Text.Json.JsonSerializer.Serialize(workspace.Current.Snapshot()));
            Environment.ExitCode = 1;
        }
        finally { workspace.Current.Scripts.Stop(); lifetime.StopApplication(); }
    }
    private static async Task WaitUntil(Func<bool> condition, Task loop, CancellationToken ct)
    {
        while (!condition()) { if (loop.IsCompleted) { await loop; throw new Exception("Session ended before expected values arrived."); } await Task.Delay(20, ct); }
    }
    private static async Task Canceled(Task loop, CancellationToken ct) { try { await loop; } catch (OperationCanceledException) when (ct.IsCancellationRequested) { } }
    private sealed class Model : IPlcModel
    {
        public IReadOnlyList<IPlcVariable> Variables { get; }
        public Model(SyncConfiguration config) => Variables = config.Fields.Select(f => (IPlcVariable)(f.Type switch
        {
            PlcType.BOOL => (IPlcVariable)new PlcVariable<bool>(f), PlcType.SINT => new PlcVariable<sbyte>(f), PlcType.INT => new PlcVariable<short>(f),
            PlcType.DINT => new PlcVariable<int>(f), PlcType.LINT => new PlcVariable<long>(f), PlcType.REAL => new PlcVariable<float>(f), PlcType.STRING => new PlcVariable<string>(f),
        PlcType.USINT => new PlcVariable<byte>(f), PlcType.UINT => new PlcVariable<ushort>(f), PlcType.UDINT => new PlcVariable<uint>(f), PlcType.ULINT => new PlcVariable<ulong>(f),
        PlcType.DT => new PlcVariable<PlcDateTime>(f), PlcType.TIME => new PlcVariable<PlcTime>(f),
        PlcType.LREAL => new PlcVariable<double>(f), PlcType.TIME32 => new PlcVariable<PlcTime32>(f), PlcType.LTIME => new PlcVariable<PlcLongTime>(f), PlcType.LDT => new PlcVariable<PlcLongDateTime>(f), _ => throw new ArgumentException()
        })).ToArray();
        public object Get(string path) => Variables.Single(v => v.Map.Path == path).BoxedValue;
        public void Set(string path, object value)
        {
            var variable = Variables.Single(v => v.Map.Path == path);
            switch (variable) { case PlcVariable<float> f: f.Value = (float)value; break; case PlcVariable<string> s: s.Value = (string)value; break; case PlcVariable<byte> v: v.Value = (byte)value; break; case PlcVariable<ushort> v: v.Value = (ushort)value; break;
                case PlcVariable<uint> v: v.Value = (uint)value; break; case PlcVariable<ulong> v: v.Value = (ulong)value; break;
                case PlcVariable<PlcDateTime> v: v.Value = (PlcDateTime)value; break; case PlcVariable<PlcTime> v: v.Value = (PlcTime)value; break;
                default: throw new ArgumentException(); }
        }
    }
}
