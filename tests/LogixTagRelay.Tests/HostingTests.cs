using LogixTagRelay;
using LogixTagRelay.Design;
using LogixTagRelay.Emulation;
using LogixTagRelay.Opener;

static class HostingTests
{
    private static SyncConfiguration Config(string path, bool implicitIo = false, ExplicitOptions? options = null)
    {
        List<FieldMap> fields = [new(path, PlcType.DINT, Direction.Bidirectional, 24)];
        return new(SyncConfiguration.ComputeHash(fields), fields, 28, 28, Explicit: options ?? new(IntervalMs: 5), Implicit: implicitIo ? new(RpiMs: 5) : null);
    }
    private static async Task Until(Func<bool> ready)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        while (!ready()) await Task.Delay(5, timeout.Token);
    }
    public static async Task Run(Action<bool, string> check)
    {
        var retry = new RecoveryOptions(InitialDelayMs: 5, MaximumDelayMs: 10, ConnectionTimeoutMs: 200, StaleTimeoutMs: 200);
        var config = Config("Command"); var memory = new PlcMemory(config); var model = new TestModel(config);
        var command = (PlcVariable<int>)model.Variables[0]; command.Value = 42;
        int attempts = 0;
        var host = new RelayHost();
        var first = host.AddExplicit("commands", config, model, _ => ValueTask.FromResult<IExplicitTransport>(new FaultTransport(memory, failRead: Interlocked.Increment(ref attempts) <= 2)), retry);
        var otherConfig = Config("Status"); var otherMemory = new PlcMemory(otherConfig); otherMemory.Set("Status", 7);
        var otherModel = new TestModel(otherConfig);
        var other = host.AddExplicit("fast-status", otherConfig, otherModel, _ => ValueTask.FromResult<IExplicitTransport>(new MemoryExplicitTransport(otherMemory)), retry);
        var ioConfig = Config("IoValue", true); var ioMemory = new PlcMemory(ioConfig); var ioPlc = new ImplicitPlc(ioMemory);
        var io = new ImplicitContractSession(ioConfig, new TestModel(ioConfig), _ => ValueTask.FromResult<IImplicitTransport>(new MemoryImplicitTransport(ioPlc)), retry);
        host.Add("io", io);
        bool reject = false;
        try { host.Add("io2", new ImplicitContractSession(ioConfig, new TestModel(ioConfig), _ => ValueTask.FromResult<IImplicitTransport>(new MemoryImplicitTransport(ioPlc)))); }
        catch (ArgumentException) { reject = true; }
        check(reject, "multi-contract host enforces one implicit contract");
        reject = false;
        try { host.AddExplicit("duplicate-owner", config, new TestModel(config), _ => ValueTask.FromResult<IExplicitTransport>(new MemoryExplicitTransport(memory))); }
        catch (ArgumentException) { reject = true; }
        check(reject, "host rejects overlapping write ownership on the same PLC");
        using (var stop = new CancellationTokenSource())
        {
            var run = host.RunAsync(stop.Token);
            await Until(() => first.Diagnostics.Snapshot.SuccessfulUpdates > 0 && other.Diagnostics.Snapshot.SuccessfulUpdates > 2 && io.Diagnostics.Snapshot.SuccessfulUpdates > 2);
            check(first.Diagnostics.Snapshot.Recoveries == 2 && first.Diagnostics.Snapshot.WritesSuspended && command.Dirty && (int)memory.Read("Command") == 0,
                "explicit reconnect reads fresh values without replaying suspended local edits");
            check((int)otherModel.Variables[0].BoxedValue == 7 && io.Diagnostics.Snapshot.State == SessionState.Running, "multiple explicit contracts run alongside the single implicit contract independently");
            first.ResumeWrites(); await Until(() => !command.Dirty && (int)memory.Read("Command") == 42);
            check(!first.Diagnostics.Snapshot.WritesSuspended && first.Diagnostics.Snapshot.LastSuccessfulUpdate is not null && first.Diagnostics.Snapshot.DataAge is not null,
                "consumer resumes pending writes and can inspect unified freshness diagnostics");
            stop.Cancel(); await run;
            check(host.Sessions.Values.All(s => s.Diagnostics.Snapshot.State == SessionState.Stopped), "host cancellation awaits owned sessions and marks them stopped");
        }
        var implicitConfig = Config("Shared", true); var implicitMemory = new PlcMemory(implicitConfig); var plc = new ImplicitPlc(implicitMemory);
        var implicitModel = new TestModel(implicitConfig); var shared = (PlcVariable<int>)implicitModel.Variables[0]; shared.Value = 55;
        int implicitAttempts = 0;
        var recovering = new ImplicitContractSession(implicitConfig, implicitModel,
            _ => ValueTask.FromResult<IImplicitTransport>(new FaultImplicit(plc, implicitMemory, Interlocked.Increment(ref implicitAttempts) == 1)), retry);
        using (var stop = new CancellationTokenSource())
        {
            var run = recovering.RunAsync(stop.Token);
            await Until(() => recovering.Diagnostics.Snapshot.Recoveries > 0 && recovering.Diagnostics.Snapshot.State == SessionState.Running);
            check(shared.Dirty && shared.Value == 55 && shared.ObservedValue == 66 && (int)implicitMemory.Read("Shared") == 66,
                "implicit recovery rejects uncertain command replay and preserves independent PLC changes");
            recovering.ResumeWrites(); await Until(() => !shared.Dirty && (int)implicitMemory.Read("Shared") == 55);
            check(implicitAttempts >= 2 && recovering.Diagnostics.Snapshot.Failures == 1, "implicit recovery creates a fresh transport and resumes after a matching new baseline");
            stop.Cancel(); await run;
        }
        var codec = new ImplicitImage(implicitConfig, new TestModel(implicitConfig)); var old = plc.Output(); var request = codec.BeginResynchronization();
        check(!codec.ReceivePlcToApp(old, DateTimeOffset.UtcNow), "resynchronization rejects pre-recovery PLC snapshots");
        var baseline = plc.Exchange(request); check(codec.ReceivePlcToApp(baseline, DateTimeOffset.UtcNow), "resynchronization accepts only its echoed nonce baseline");
        var another = new ImplicitImage(implicitConfig, new TestModel(implicitConfig)); another.BeginResynchronization();
        check(!another.ReceivePlcToApp(baseline, DateTimeOffset.UtcNow), "old connection nonce cannot satisfy a new handshake");
        var bundle = TypesAndEventsTests.Bundle(); var wrapper = new RllSubset(bundle); wrapper.Set("LogixRelay_PS_ApplyCommands", 1); wrapper.Scan();
        var wrapperCodec = new ImplicitImage(bundle.Configuration, new TestModel(bundle.Configuration)); var handshake = wrapperCodec.BeginResynchronization();
        wrapper.Bytes("LogixRelay:I.Data", handshake); wrapper.Scan();
        var response = wrapper.Bytes("LogixRelay:O.Data");
        check(ImplicitImage.ReadInt(response, 20) == 3 && ImplicitImage.ReadInt(response, 16) == ImplicitImage.ReadInt(handshake, 16) && wrapperCodec.ReceivePlcToApp(response, DateTimeOffset.UtcNow),
            "emitted PLC wrapper acknowledges recovery nonce without merging any bootstrap payload");
        wrapper.Bytes("LogixRelay:I.Data", wrapperCodec.BuildAppToPlc()); wrapper.Scan();
        check(ImplicitImage.ReadInt(wrapper.Bytes("LogixRelay:O.Data"), 20) == 1 && wrapperCodec.ReceivePlcToApp(wrapper.Bytes("LogixRelay:O.Data"), DateTimeOffset.UtcNow),
            "emitted wrapper resumes normal acknowledgements after the recovery baseline");
        await TriggerReceipt(check, retry);
        var isolation = new RelayHost(); var badConfig = Config("Broken");
        var broken = isolation.AddExplicit("broken", badConfig, new TestModel(badConfig), _ => ValueTask.FromException<IExplicitTransport>(new FormatException("Schema differs")), retry);
        var healthy = isolation.AddExplicit("healthy", otherConfig, new TestModel(otherConfig), _ => ValueTask.FromResult<IExplicitTransport>(new MemoryExplicitTransport(otherMemory)), retry);
        using (var stop = new CancellationTokenSource())
        {
            var run = isolation.RunAsync(stop.Token);
            await Until(() => broken.Diagnostics.Snapshot.State == SessionState.Faulted && healthy.Diagnostics.Snapshot.SuccessfulUpdates > 2);
            check(broken.Diagnostics.Snapshot.Recoveries == 0 && healthy.Diagnostics.Snapshot.State == SessionState.Running, "non-transient faults do not retry or stop unrelated contracts");
            stop.Cancel(); try { await run; } catch (FormatException) { }
        }
        int timeoutAttempts = 0;
        var timeoutSession = new ExplicitContractSession(otherConfig, new TestModel(otherConfig), _ => ValueTask.FromResult<IExplicitTransport>(new FaultTransport(otherMemory, cancelRead: Interlocked.Increment(ref timeoutAttempts) == 1)), retry);
        using (var stop = new CancellationTokenSource())
        {
            var run = timeoutSession.RunAsync(stop.Token); await Until(() => timeoutSession.Diagnostics.Snapshot.SuccessfulUpdates > 0);
            check(timeoutSession.Diagnostics.Snapshot.Recoveries == 1 && timeoutAttempts == 2, "transport-local cancellation/timeouts recover without treating them as user shutdown");
            stop.Cancel(); await run;
        }
        var deadlineSession = new ExplicitSession(otherConfig with { Explicit = new(IntervalMs: 1) }, new TestModel(otherConfig), new DelayedTransport(otherMemory));
        deadlineSession.Diagnostics.Changed += (_,_) => throw new Exception("Observer failure");
        await deadlineSession.SynchronizeOnceAsync();
        check(deadlineSession.Diagnostics.Snapshot.MissedDeadlines == 1 && deadlineSession.Diagnostics.Snapshot.MaximumCycleDuration >= TimeSpan.FromMilliseconds(8), "diagnostics measure deadlines and isolate observer exceptions");
        var stoppedConfig = Config("StoppedPending"); var stoppedMemory = new PlcMemory(stoppedConfig); var stoppedModel = new TestModel(stoppedConfig);
        var pending = (PlcVariable<int>)stoppedModel.Variables[0]; pending.Value = 9;
        var stoppedSession = new ExplicitContractSession(stoppedConfig, stoppedModel, async ct => { await Task.Delay(1000,ct); return new MemoryExplicitTransport(stoppedMemory); }, retry);
        using (var stop = new CancellationTokenSource(10)) await stoppedSession.RunAsync(stop.Token);
        check(pending.Dirty && stoppedSession.Diagnostics.Snapshot.WritesSuspended && stoppedSession.Diagnostics.Snapshot.State == SessionState.Stopped, "cancellation preserves pending values and suspends them across a later restart");
        check(await OpenerAdapter.ProbeWorkerAsync() == 2, "owned OpENer child entry point loads native ABI without binding EtherNet/IP ports");
    }
    private static async Task TriggerReceipt(Action<bool, string> check, RecoveryOptions retry)
    {
        var config = Config("Setpoint", options: new(IntervalMs: 5, TriggerTag: "Request", TriggerPollMs: 5, HandledTriggerTag: "Handled", ResponseTag: "Response"));
        var memory = new PlcMemory(config); memory.Set("Request", 11); var model = new TestModel(config); ((PlcVariable<int>)model.Variables[0]).Value = 3;
        int attempts = 0, writes = 0;
        var session = new ExplicitContractSession(config, model, _ => ValueTask.FromResult<IExplicitTransport>(new FaultTransport(memory,
            failMirror: Interlocked.Increment(ref attempts) == 1, dataWrite: () => Interlocked.Increment(ref writes))), retry);
        using var stop = new CancellationTokenSource(); var run = session.RunAsync(stop.Token);
        await Until(() => (int)memory.Read("Handled") == 11 && session.Diagnostics.Snapshot.State == SessionState.Running);
        check(writes == 1 && (int)memory.Read("Response") == 11 && session.Diagnostics.Snapshot.LastTrigger is { Succeeded: true, NotificationsDelivered: true },
            "recovery repairs an interrupted trigger receipt without repeating completed data writes");
        session.ResumeWrites(); await Task.Delay(30);
        check(writes == 1, "handled trigger mirror prevents replay of a held request after reconnect");
        stop.Cancel(); await run;
    }
    private sealed class DelayedTransport(PlcMemory memory) : IExplicitTransport
    {
        public async ValueTask<object> ReadAsync(string path, PlcType type, CancellationToken ct) { await Task.Delay(10, ct); return memory.Read(path); }
        public ValueTask WriteAsync(string path, PlcType type, object value, CancellationToken ct) => ValueTask.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    private sealed class FaultTransport(PlcMemory memory, bool failRead = false, bool failMirror = false, Action? dataWrite = null, bool cancelRead = false) : IExplicitTransport
    {
        private readonly MemoryExplicitTransport inner = new(memory);
        public ValueTask<object> ReadAsync(string path, PlcType type, CancellationToken ct) => cancelRead ? ValueTask.FromException<object>(new TaskCanceledException("Transport timed out")) : failRead ? ValueTask.FromException<object>(new IOException("Read interrupted")) : inner.ReadAsync(path, type, ct);
        public ValueTask WriteAsync(string path, PlcType type, object value, CancellationToken ct)
        {
            if (failMirror && path == "Handled") return ValueTask.FromException(new IOException("Receipt interrupted"));
            if (path == "Setpoint") dataWrite?.Invoke(); return inner.WriteAsync(path, type, value, ct);
        }
        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }
    private sealed class FaultImplicit(ImplicitPlc plc, PlcMemory memory, bool fail) : IImplicitTransport
    {
        private int exchanges;
        public ValueTask<byte[]?> ExchangeAsync(byte[] image, CancellationToken ct)
        {
            byte[] result = plc.Exchange(image);
            if (fail && ++exchanges == 2) { memory.Set("Shared", 66); throw new IOException("ACK lost after PLC applied the command"); }
            return ValueTask.FromResult<byte[]?>(result);
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
