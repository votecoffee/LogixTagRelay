using System.Collections.Concurrent;
using LogixTagRelay;
using LogixTagRelay.Design;
using LogixTagRelay.Emulation;
using LogixTagRelay.Logix;

static class LivenessTests
{
    private static SyncConfiguration Config(bool io = false, int heartbeatMs = 15, bool triggered = false)
    {
        List<FieldMap> fields = [new("Command", PlcType.DINT, Direction.Bidirectional, 24)];
        return new(SyncConfiguration.ComputeHash(fields), fields, 28, 28, Explicit: new(IntervalMs: 5, TriggerTag: triggered ? "Request" : null, TriggerPollMs: 100),
            Implicit: io ? new(RpiMs: 5) : null, Heartbeat: new("ApiAlive", heartbeatMs));
    }
    private static async Task Until(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(6));
        while (!condition()) await Task.Delay(3, timeout.Token);
    }
    public static async Task Run(Action<bool, string> check)
    {
        check(new HeartbeatOptions("ApiAlive").IntervalMs == 5000, "heartbeat defaults to one explicit toggle every five seconds");
        var catalog = new TagCatalog("Heartbeat", "35.03", [new("Command", "Command", "DINT", "Read/Write", []), new("ApiAlive", "ApiAlive", "BOOL", "Read/Write", [])]);
        var ordinary = Generator.Generate(catalog, [new("Command", Direction.Bidirectional)]);
        var heartbeat = Generator.Generate(catalog, [new("Command", Direction.Bidirectional)], heartbeatOptions: new("ApiAlive"));
        check(heartbeat.Configuration.SchemaHash == ordinary.Configuration.SchemaHash && heartbeat.Configuration.PlcToAppBytes == 28 && heartbeat.Configuration.Fields.All(f => f.Path != "ApiAlive"),
            "heartbeat does not enter the implicit data image or change its schema/size");
        var project = new ContractProject("Heartbeat", [new("Command", Direction.Bidirectional)], catalog, Heartbeat: new("ApiAlive"));
        check(ContractProject.FromJson(project.ToJson()).Generate().Configuration.Heartbeat!.IntervalMs == 5000, "saved projects retain explicit heartbeat settings");
        bool invalid = false; try { Generator.Generate(catalog, [new("ApiAlive", Direction.AppToPlc)], heartbeatOptions: new("ApiAlive")); } catch (ArgumentException) { invalid = true; }
        check(invalid, "heartbeat BOOL cannot also be owned by the data contract");
        var config = Config(triggered: true); var memory = new PlcMemory(config); memory.Set("ApiAlive", true);
        var trace = new TraceTransport(memory); var model = new TestModel(config);
        var session = new ExplicitContractSession(config, model, _ => ValueTask.FromResult<IExplicitTransport>(trace));
        using (var stop = new CancellationTokenSource())
        {
            var run = session.RunAsync(stop.Token); await Until(() => session.Diagnostics.Snapshot.HeartbeatTransitions >= 3);
            stop.Cancel(); await run;
        }
        var writes = trace.Writes.ToArray();
        check(writes.Length >= 3 && writes.All(w => w.Path == "ApiAlive" && w.Type == PlcType.BOOL) && (bool)writes[0].Value == false && writes.Zip(writes.Skip(1)).All(p => !Equals(p.First.Value, p.Second.Value)),
            "idle DINT-triggered contracts continue explicit heartbeat writes from the observed BOOL phase");
        check(session.Diagnostics.Snapshot.SuccessfulUpdates == 0 && session.Diagnostics.Snapshot.LastHeartbeat is not null, "heartbeat liveness is visible without inventing a triggered data update");
        var ioConfig = Config(io: true); var ioMemory = new PlcMemory(ioConfig); ioMemory.Set("ApiAlive", true); var plc = new ImplicitPlc(ioMemory);
        var ioModel = new TestModel(ioConfig); var explicitTrace = new TraceTransport(ioMemory);
        await using (var adapter = new LogixTagRelay.Opener.OpenerAdapter(ioConfig,ioModel,"unused-nic"))
            check(adapter.HeartbeatDiagnostics is not null && adapter.Configuration.PlcToAppBytes == ioConfig.PlcToAppBytes, "native adapter automatically provisions the explicit heartbeat channel without changing image size");
        var ioSession = new ImplicitContractSession(ioConfig, ioModel, _ => ValueTask.FromResult<IImplicitTransport>(new MemoryImplicitTransport(plc)),
            heartbeatConnect: _ => ValueTask.FromResult<IExplicitTransport>(explicitTrace));
        using (var stop = new CancellationTokenSource())
        {
            var run = ioSession.RunAsync(stop.Token); await Until(() => ioSession.Diagnostics.Snapshot.SuccessfulUpdates > 2 && ioSession.HeartbeatDiagnostics!.Snapshot.HeartbeatTransitions >= 3);
            stop.Cancel(); await run;
        }
        check(explicitTrace.Writes.Count >= 3 && ioSession.Diagnostics.Snapshot.HeartbeatTransitions >= 3 && ioConfig.PlcToAppBytes == 28,
            "an implicit-only contract runs its heartbeat through a separate explicit channel with independent diagnostics");
        var slowConfig = Config() with { Heartbeat = null }; var slowMemory = new PlcMemory(slowConfig); var delayed = new TraceTransport(slowMemory, delayMs: 20);
        var slow = new ExplicitSession(slowConfig, new TestModel(slowConfig), delayed);
        using (var stop = new CancellationTokenSource())
        {
            var run = slow.RunAsync(stop.Token); await Until(() => slow.Diagnostics.Snapshot.SuccessfulUpdates >= 3); stop.Cancel();
            try { await run; } catch (OperationCanceledException) { }
        }
        check(slow.Diagnostics.Snapshot.MissedIntervals > 0 && slow.Diagnostics.Snapshot.MissedDeadlines > 0 && delayed.MaximumConcurrency == 1,
            "slow intervals count skipped slots/deadlines and never dispatch overlapping work");
        var queued = new ExplicitSession(slowConfig, new TestModel(slowConfig), delayed);
        await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => queued.SynchronizeOnceAsync()));
        check(queued.Diagnostics.Snapshot.OverlappingRequests >= 3 && delayed.MaximumConcurrency == 1, "concurrent manual requests are counted and serialized");
        var retry = new RecoveryOptions(InitialDelayMs: 15, MaximumDelayMs: 100, DelayMode: ReconnectDelayMode.Fixed); int attempts = 0;
        var reconnect = new ExplicitContractSession(slowConfig, new TestModel(slowConfig), _ => ++attempts < 3 ? ValueTask.FromException<IExplicitTransport>(new IOException("Disconnected")) : ValueTask.FromResult<IExplicitTransport>(new MemoryExplicitTransport(slowMemory)), retry);
        var delays = new ConcurrentQueue<int>(); reconnect.Diagnostics.Changed += (_, s) => { if (s.State == SessionState.Recovering) delays.Enqueue(s.LastRetryDelayMs); };
        using (var stop = new CancellationTokenSource()) { var run = reconnect.RunAsync(stop.Token); await Until(() => reconnect.Diagnostics.Snapshot.SuccessfulUpdates > 0); stop.Cancel(); await run; }
        check(delays.Count == 2 && delays.All(d => d == 15) && reconnect.Diagnostics.LastException is PlcConnectionException && reconnect.Diagnostics.Snapshot.LastFailure?.Operation == PlcOperation.Connect,
            "autoreconnect exposes an adjustable fixed delay and typed initial connection failures");
        var failure = new ExplicitContractSession(slowConfig, new TestModel(slowConfig), _ => ValueTask.FromException<IExplicitTransport>(new IOException("Cannot connect")), new(AutoReconnect: false));
        using var failureTimeout = new CancellationTokenSource(2000);
        bool typedConnection = false; try { await failure.RunAsync(failureTimeout.Token); } catch (PlcConnectionException) { typedConnection = true; }
        check(typedConnection && failure.Diagnostics.Snapshot.Recoveries == 0, "disabling autoreconnect surfaces the typed initial connection exception immediately");
        var readFailure = new ExplicitSession(slowConfig, new TestModel(slowConfig), new BrokenTransport());
        PlcTagReadException? readError = null;
        try { await readFailure.SynchronizeOnceAsync(); } catch (PlcTagReadException error) { readError = error; }
        check(readError is { TagPath: "Command", Operation: PlcOperation.ReadTag } && readError.Gateway == slowConfig.Gateway && readFailure.Diagnostics.LastException == readError,
            "actual session read faults carry the requested tag, endpoint and original cause");
        var writeModel = new TestModel(slowConfig); ((PlcVariable<int>)writeModel.Variables[0]).Value = 7;
        var writeFailure = new ExplicitSession(slowConfig, writeModel, new BrokenTransport(writeOnly: true));
        PlcTagWriteException? writeError = null;
        try { await writeFailure.SynchronizeOnceAsync(); } catch (PlcTagWriteException error) { writeError = error; }
        check(writeError is { TagPath: "Command", Operation: PlcOperation.WriteTag } && writeError.InnerException is IOException,
            "actual session write faults are differentiated from successful connection and read phases");
        var native = LogixFaults.Translate(new libplctag.LibPlcTagException(libplctag.Status.ErrorNotFound), "192.0.2.1", "1,0", "Missing", PlcOperation.InitializeTag);
        check(native is PlcTagReadException && native.TagPath == "Missing" && native.NativeStatus == (int)libplctag.Status.ErrorNotFound && !native.IsTransient,
            "missing-tag initialization preserves driver status and is distinct from endpoint connection failure");
        var write = LogixFaults.Translate(new libplctag.LibPlcTagException(libplctag.Status.ErrorNotAllowed), "192.0.2.1", "1,0", "Protected", PlcOperation.WriteTag);
        check(write is PlcTagWriteException && write.FailureKind == PlcFailureKind.AccessDenied && !write.IsTransient, "tag write permission errors have a distinct typed fault and do not retry forever");
        await EndpointProbeTests.Run(check);
    }
    private sealed class BrokenTransport(bool writeOnly = false) : IExplicitTransport
    {
        public ValueTask<object> ReadAsync(string path, PlcType type, CancellationToken ct) => writeOnly ? ValueTask.FromResult<object>(0) : ValueTask.FromException<object>(new IOException("Tag read failed"));
        public ValueTask WriteAsync(string path, PlcType type, object value, CancellationToken ct) => ValueTask.FromException(new IOException("Tag write failed"));
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    private sealed class TraceTransport(PlcMemory memory, int delayMs = 0) : IExplicitTransport
    {
        private readonly MemoryExplicitTransport inner = new(memory); private int active, maximum;
        public int MaximumConcurrency => maximum;
        public ConcurrentQueue<(string Path, PlcType Type, object Value)> Writes { get; } = new();
        public async ValueTask<object> ReadAsync(string path, PlcType type, CancellationToken ct)
        {
            int now = Interlocked.Increment(ref active); maximum = Math.Max(maximum, now);
            try { if (delayMs > 0) await Task.Delay(delayMs, ct); return await inner.ReadAsync(path, type, ct); }
            finally { Interlocked.Decrement(ref active); }
        }
        public async ValueTask WriteAsync(string path, PlcType type, object value, CancellationToken ct) { await inner.WriteAsync(path, type, value, ct); Writes.Enqueue((path, type, value)); }
        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }
}
