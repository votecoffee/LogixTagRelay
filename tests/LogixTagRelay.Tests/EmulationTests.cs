using LogixTagRelay;
using LogixTagRelay.Design;
using LogixTagRelay.Emulation;

static class EmulationTests
{
    public static async Task Run(GeneratedBundle bundle, Action<bool, string> check)
    {
        void Reject<T>(Action action) where T : Exception
        { try { action(); } catch (T) { return; } throw new Exception("Expected " + typeof(T).Name); }
        var memory = new PlcMemory(bundle.Configuration);
        memory.Set("Status.Speed", 15f);
        var model = new TestModel(bundle.Configuration);
        var command = (PlcVariable<float>)model.Variables.Single(v => v.Map.Path == "Command.Setpoint"); command.Value = 4f;
        await using var transport = new MemoryExplicitTransport(memory);
        await new ExplicitSession(bundle.Configuration, model, transport).SynchronizeOnceAsync();
        check((float)memory.Read("Command.Setpoint") == 4f && (float)model.Variables.Single(v => v.Map.Path == "Status.Speed").BoxedValue == 15f && !command.Dirty,
            "emulator explicit session reads PLC memory and publishes API commands");
        check(memory.Snapshot().Tags.Single(t => t.Map.Path == "Command.Setpoint").LastApiValue == "4", "emulator records API command separately for GUI inspection");
        Reject<InvalidOperationException>(() => memory.Apply([new("Status.Speed", 99f)], "API", fromApi: true));
        check((float)memory.Read("Status.Speed") == 15f, "emulator API cannot write PLC-owned explicit fields");
        var triggered = bundle.Configuration with { Explicit = new(TriggerTag: "Trigger", HandledTriggerTag: "Handled", ResponseTag: "Response") };
        var triggeredMemory = new PlcMemory(triggered); triggeredMemory.Set("Trigger", 200000000);
        var triggeredModel = new TestModel(triggered);
        await using var triggeredTransport = new MemoryExplicitTransport(triggeredMemory);
        var result = await new ExplicitSession(triggered, triggeredModel, triggeredTransport).PollTriggerOnceAsync();
        check(result?.Succeeded == true && (int)triggeredMemory.Read("Handled") == 200000000 && (int)triggeredMemory.Read("Response") == 200000000,
            "emulator includes DINT trigger, handled and signed response memory");

        var plc = new ImplicitPlc(memory); var codec = new ImplicitImage(bundle.Configuration, model);
        codec.ReceivePlcToApp(plc.Exchange(codec.BuildBootstrap()), DateTimeOffset.UtcNow);
        memory.Set("Command.Setpoint", BitConverter.Int32BitsToSingle(0x40800001));
        command.Value = BitConverter.Int32BitsToSingle(0x40810002);
        var frame = codec.BuildAppToPlc();
        var response = plc.Exchange(frame);
        codec.ReceivePlcToApp(response, DateTimeOffset.UtcNow);
        check(BitConverter.SingleToInt32Bits(command.Value) == 0x40810001 && !command.Dirty && plc.Status.ConflictBytes == 1,
            "emulator implicit exchange preserves PLC-winning bytes and reconciles typed API values");
        var revision = memory.Snapshot().Revision;
        check(plc.Exchange(frame).SequenceEqual(response) && memory.Snapshot().Revision == revision && plc.Status.Result == "Duplicate", "emulator duplicate implicit packet is side-effect free");
        var next = codec.BuildAppToPlc(); ImplicitImage.WriteInt(next, 16, 777);
        check(plc.Exchange(next).SequenceEqual(response) && plc.Status.Result == "Baseline mismatch", "emulator rejects stale implicit baseline without writes");
        plc.ApplyCommands = false; ImplicitImage.WriteInt(next, 16, plc.Status.Sequence);
        check(plc.Exchange(next).SequenceEqual(response) && plc.Status.Result == "Commands disabled", "emulator can hold implicit commands for manual conflict testing");
        var parityMemory = new PlcMemory(bundle.Configuration); parityMemory.Set("Command.Setpoint", 1f);
        var parityPlc = new ImplicitPlc(parityMemory); var wrapper = new RllSubset(bundle);
        wrapper.Set("Command.Setpoint", 0x3f800000); wrapper.Set("LogixRelay_PS_ApplyCommands", 1); wrapper.Scan();
        check(parityPlc.Output().SequenceEqual(wrapper.Bytes("LogixRelay:O.Data")), "emulator startup image matches the emitted PLC wrapper model");
        var parityModel = new TestModel(bundle.Configuration); var parityCodec = new ImplicitImage(bundle.Configuration, parityModel);
        parityCodec.ReceivePlcToApp(parityPlc.Output(), DateTimeOffset.UtcNow);
        ((PlcVariable<float>)parityModel.Variables.Single(v => v.Map.Path == "Command.Setpoint")).Value = BitConverter.Int32BitsToSingle(0x3f810002);
        var parityRequest = parityCodec.BuildAppToPlc(); parityMemory.Set("Command.Setpoint", BitConverter.Int32BitsToSingle(0x3f800001));
        wrapper.Set("Command.Setpoint", 0x3f800001); wrapper.Bytes("LogixRelay:I.Data", parityRequest); wrapper.Scan();
        check(parityPlc.Exchange(parityRequest).SequenceEqual(wrapper.Bytes("LogixRelay:O.Data")), "emulator conflict exchange matches generated wrapper plus ST AOI execution offline");
        List<FieldMap> invalidFields = [new("Text", PlcType.STRING, Direction.AppToPlc, 24, StringCapacity: 256), new("Number", PlcType.DINT, Direction.AppToPlc, 284)];
        var invalidConfig = new SyncConfiguration(SyncConfiguration.ComputeHash(invalidFields), invalidFields, 288, 288, Implicit: new());
        var invalidMemory = new PlcMemory(invalidConfig); var invalidPlc = new ImplicitPlc(invalidMemory); var invalidModel = new TestModel(invalidConfig);
        var invalidCodec = new ImplicitImage(invalidConfig, invalidModel); var savedOutput = invalidPlc.Output(); invalidCodec.ReceivePlcToApp(savedOutput, DateTimeOffset.UtcNow);
        ((PlcVariable<string>)invalidModel.Variables[0]).Value = "X"; ((PlcVariable<int>)invalidModel.Variables[1]).Value = 99;
        var invalidRequest = invalidCodec.BuildAppToPlc(); invalidMemory.Set("Text", new string('P',256));
        check(invalidPlc.Exchange(invalidRequest).SequenceEqual(savedOutput) && invalidPlc.Status.Result == "Invalid STRING length" && (int)invalidMemory.Read("Number") == 0 && ((string)invalidMemory.Read("Text")).Length == 256,
            "emulator rejects invalid byte-merged STRING LEN without writes or ACK");

        var fields = new List<FieldMap> { new("Command", PlcType.REAL, Direction.AppToPlc, 24), new("Actual", PlcType.REAL, Direction.PlcToApp, 28),
            new("InPosition", PlcType.BOOL, Direction.PlcToApp, 32), new("Counter", PlcType.LINT, Direction.PlcToApp, 36),
            new("Labels[1,2]", PlcType.STRING, Direction.PlcToApp, 44, StringCapacity: 12) };
        var config = new SyncConfiguration(SyncConfiguration.ComputeHash(fields), fields, 60, 60, Explicit: new(), Implicit: new());
        var scriptMemory = new PlcMemory(config); scriptMemory.Set("Command", 3f); scriptMemory.SetText("Counter", "9007199254740993");
        check(TagText.Format(scriptMemory.Read("Counter")) == "9007199254740993", "emulator GUI text preserves full LINT precision");
        var runner = new ScriptRunner(scriptMemory);
        runner.Configure("""
            const target = plc.get('Command'), actual = plc.get('Actual');
            const step = Math.sign(target - actual) * Math.min(Math.abs(target - actual), 10 * dt);
            plc.set('Actual', actual + step);
            plc.set('InPosition', Math.abs(target - (actual + step)) < 0.01);
            plc.set('Counter', plc.get('Counter') + 1n);
            if (plc.changed('Command')) plc.log('New command: ' + target);
            state.ticks = (state.ticks || 0) + 1;
            """);
        for (int tick = 0; tick < 3; tick++) if (!runner.Step()) throw new Exception("Motion script failed: " + runner.Status.Error);
        check((float)scriptMemory.Read("Actual") == 3f && (bool)scriptMemory.Read("InPosition"),
            "JavaScript motion script moves actual position and sets in-position in response to command");
        check((long)scriptMemory.Read("Counter") == 9007199254740996L && runner.Status.StateJson.Contains("3") && runner.Status.Log.Count == 1,
            "scripts retain JSON state, detect changes and preserve LINT with BigInt");
        runner.Configure("plc.set('Actual', 999); plc.set('Labels[1,2]', 'too long for twelve');");
        check(!runner.Step() && (float)scriptMemory.Read("Actual") == 3f && runner.Status.Error is not null, "invalid script write rolls back the whole emulator tick");
        runner.Configure("plc.set('Actual', 999); while (true) {}", start: true);
        check(!runner.Step() && !runner.Status.Running && (float)scriptMemory.Read("Actual") == 3f, "runaway script is bounded, stops visibly and publishes no partial writes");
        runner.Configure("plc.set('Actual', 999); throw new Error('test failure');");
        check(!runner.Step() && runner.Status.Error!.Contains("test failure") && (float)scriptMemory.Read("Actual") == 3f, "script errors retain prior PLC memory and expose diagnostics");
        runner.Configure("plc.set('Actual', 999); input.state = undefined;");
        check(!runner.Step() && (float)scriptMemory.Read("Actual") == 3f, "malformed script state is rejected before any memory commit");
        runner.Configure("plc.set('Actual', typeof System === 'undefined' && typeof fetch === 'undefined' ? 42 : -1);");
        check(runner.Step() && (float)scriptMemory.Read("Actual") == 42f, "scripts receive no CLR or network objects");
        var before = scriptMemory.Snapshot(); scriptMemory.Set("Actual", 1f);
        check(!scriptMemory.Apply([new("Actual", 7f)], "Script", before.Revision) && (float)scriptMemory.Read("Actual") == 1f, "stale script transaction cannot overwrite intervening memory changes");
        long unchangedRevision = scriptMemory.Snapshot().Revision;
        scriptMemory.Apply([new("Command", 3f)], "API cyclic", fromApi: true);
        check(scriptMemory.Snapshot().Revision == unchangedRevision && scriptMemory.Snapshot().Tags.Single(t => t.Map.Path == "Command").LastApiWriteAt is not null,
            "unchanged cyclic API writes retain diagnostics without invalidating script snapshots");
        runner.Configure("plc.set('Actual', plc.get('Actual') + 1);");
        bool committedEveryTick = true;
        using (var stop = new CancellationTokenSource())
        {
            var writer = Task.Run(async () =>
            {
                while (!stop.IsCancellationRequested) { scriptMemory.Apply([new("Command", 3f)], "API cyclic", fromApi: true); await Task.Delay(1); }
            });
            try { for (int i = 0; i < 20; i++) committedEveryTick &= runner.Step(); }
            finally { stop.Cancel(); await writer; }
        }
        check(committedEveryTick && (float)scriptMemory.Read("Actual") == 21f, "concurrent unchanged API traffic cannot starve scripted PLC updates");
        Reject<FormatException>(() => scriptMemory.SetText("InPosition", "not bool"));
        Reject<OverflowException>(() => scriptMemory.SetText("Counter", "9223372036854775808"));
        check(true, "manual emulator edits enforce BOOL and LINT type bounds");
    }
}
