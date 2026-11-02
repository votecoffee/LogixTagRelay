# Heartbeat, errors and interval diagnostics

## API heartbeat

Configure an existing writable PLC BOOL outside the contract's data selections:

```csharp
config = config with { Heartbeat = new HeartbeatOptions("ApiAlive") };
```

The default is one toggle every **5000 ms**, so a complete false/true cycle takes ten
seconds. Startup/reconnect reads the current BOOL, then waits one interval before
writing its opposite. Only successful writes advance the local phase; an uncertain
write causes a fresh phase read on reconnect. Observe edges in PLC watchdog logic,
rather than treating a constant true level as evidence of liveness.

Heartbeat always uses **explicit BOOL reads/writes**, including for an application
with just one implicit contract. It never occupies the implicit SINT image, changes
its field hash or increases its byte count. The 480-byte image cap remains unchanged.
The PLC BOOL must already exist; the generated import does not create application tags.

Explicit sessions schedule the heartbeat while DINT-triggered groups wait for requests.
`OpenerAdapter` automatically supplies a separate `LogixTransport` heartbeat channel.
For custom implicit transports, provide `heartbeatConnect`:

```csharp
var io = new ImplicitContractSession(config, tags, connectImplicit,
    heartbeatConnect: ct => ValueTask.FromResult<IExplicitTransport>(
        new LogixTransport(config.Gateway, config.Route)));
```

The implicit data channel and explicit heartbeat channel recover independently. An
implicit outage does not stop a reachable heartbeat; a heartbeat tag/route fault does
not stop healthy implicit updates. Inspect `HeartbeatDiagnostics`, or the parent
snapshot's heartbeat state/failure/value/timestamp/counter fields. A terminal channel
error is reported by the combined run task when the other channel stops, like host
fault isolation. `EmulatorClient.CreateImplicitSession` and `RunImplicitAsync` wire both
channels automatically; `LastImplicitSession` exposes diagnostics and `ResumeWrites()`.

Use a single owner for a heartbeat tag on each PLC. `RelayHost` rejects duplicate write
ownership. The browser and saved `.ltrx.json` projects retain heartbeat tag/interval
settings. `ExplicitHeartbeatSession` can also run a dedicated liveness channel.

`samples/LivenessDemo` demonstrates one implicit contract plus explicit heartbeat with
the five-second default. `--quick` changes only the example's interval for local checks.

## Initial connection and tag failures

`LogixTransport.ConnectAsync` checks TCP reachability and a short EtherNet/IP Register
Session exchange before tag access. It unregisters that probe session. This establishes
that the Ethernet endpoint accepts EtherNet/IP; it cannot prove the configured CPU route,
tag existence or permissions. Subsequent native tag initialization/read/write failures
retain that separate operation phase.

```csharp
await using var transport = new LogixTransport(config.Gateway, config.Route);
try { await transport.ConnectAsync(cancellationToken); }
catch (PlcConnectionException error)
{
    Log(error.FailureKind, error.Operation, error.Gateway, error.SocketError);
}
try { await new ExplicitSession(config, tags, transport).SynchronizeOnceAsync(cancellationToken); }
catch (PlcTagReadException error) { LogRead(error.TagPath, error.NativeStatusName); }
catch (PlcTagWriteException error) { LogWrite(error.TagPath, error.NativeStatusName); }
```

`PlcCommunicationException` exposes endpoint, route, tag when known, operation,
failure kind, retryability, native numeric/name status and socket error. Its inner
exception retains the original cause. Operations distinguish TCP connection,
registration, tag initialization/read/write, trigger/response, heartbeat and implicit
worker/handshake/image exchange stages. Failures during native controller-session setup
can remain `PlcConnectionException` even when first detected while accessing a tag.

Host/network unreachable is used only when the OS reports it. Connection refusal,
name resolution and timeouts remain distinct. A timeout alone does not establish that
the PLC is unreachable. Native status can also be ambiguous: invalid path/parameter
does not prove that a specific tag was deleted. The pinned libplctag.NET 1.5.2 wrapper
only includes native status as an exact enum-name message; unknown messages retain
their original text without inventing a numeric status.

During automatic recovery, subscribe to `Diagnostics.FaultOccurred` for structured
faults, `Snapshot.LastFailure` for a serializable record, and `LastException` for the
actual typed exception. `AutoReconnect: false` allows a standalone run to fail directly.
Caller-requested cancellation stays cancellation and does not fabricate a connection fault.

## Adjustable reconnect delay

```csharp
var recovery = new RecoveryOptions(
    InitialDelayMs: 1000,
    DelayMode: ReconnectDelayMode.Fixed,
    AutoReconnect: true);
```

Fixed mode uses that delay for every retry. Exponential mode grows from InitialDelayMs
to MaximumDelayMs. MaximumAttempts bounds recovery attempts (zero is unlimited).
Snapshot LastRetryDelayMs/NextRetryAt report the chosen delay. Non-transient missing
tag, access and protocol errors fault instead of endlessly reconnecting. Pending
application writes still use the configured Suspend/Retry policy; heartbeat has no
queued application command to replay and re-seeds its BOOL after connection recovery.

## Interval counters

Scheduling uses monotonic time and serialized operations. Expired starts are skipped
instead of dispatching catch-up bursts. Diagnostics distinguish:

- `MissedDeadlines`: successful data cycles whose work duration exceeded the interval.
- `MissedIntervals`: scheduled starts skipped while prior work occupied their slots.
- `OverlappingRequests`: external/manual calls that arrived while the session gate was
  busy and were queued. Actual communications remain serialized.
- `LastStartLateness`/`MaximumStartLateness`: observed start delay against the cadence.
- `HeartbeatMissedIntervals`: skipped heartbeat slots, separate from data update slots.

Counts are per session and do not promise real-time behavior. Slow PLC operations or
callbacks can delay the next explicit heartbeat; the counters expose those delays.
Separate implicit heartbeat channels expose their own diagnostics independently.

The protocol and native status references are the primary
[libplctag session implementation](https://github.com/libplctag/libplctag/blob/release/src/libplctag/protocols/ab/session.c)
and [libplctag API error reference](https://github.com/libplctag/libplctag/wiki/API).
Live PLC/controller acceptance is still required.
