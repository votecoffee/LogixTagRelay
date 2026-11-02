# Hosting, diagnostics and recovery

`RelayHost` runs multiple independent explicit contracts with at most one implicit
contract. Each explicit group owns its interval/trigger settings and transport lifetime.
The implicit contract uses one Generic Ethernet Module and one image per direction,
at most 480 bytes including the 24-byte application header. No splitting or aggregation
beyond that limit is implemented. Cyclic EtherNet/IP I/O uses UDP; TCP is used for
explicit messaging/connection setup ([ODVA](https://www.odva.org/wp-content/uploads/2020/05/PUB00213R0_EtherNetIP_Developers_Guide.pdf)).

```csharp
var host = new RelayHost();
var status = host.AddExplicit("status", statusConfig, statusTags,
    _ => ValueTask.FromResult<IExplicitTransport>(
        new LogixTransport(statusConfig.Gateway, statusConfig.Route)));
host.AddExplicit("commands", commandConfig, commandTags,
    _ => ValueTask.FromResult<IExplicitTransport>(
        new LogixTransport(commandConfig.Gateway, commandConfig.Route)));
await using var io = new OpenerAdapter(ioConfig, ioTags,
    nicNameOrIndex: "Ethernet", deviceSerialNumber: 1234);
host.Add("io", io);
status.Diagnostics.Changed += (_, snapshot) => UpdateStatus(snapshot);
await host.RunAsync(cancellationToken);
```

Register contracts before starting. Names must be unique. Two contracts targeting the
same PLC/route cannot both own the same writable member or explicit reply control.
Read-only overlap is allowed. Faulted contracts remain visible in `host.Sessions`;
other contracts continue. Cancellation waits for all sessions and owned transports.
If a group faults permanently, the host task reports that error when its remaining
groups stop. Subscribe to diagnostics for immediate per-contract fault handling.

## Recovery policy

`ExplicitContractSession` recreates its transport on transient communication failures.
`ImplicitContractSession` recreates its transport and requests a new PLC baseline.
Factories must return newly owned transports and honor their cancellation token.
Format/schema/argument errors fault the group instead of retrying indefinitely.

`RecoveryOptions` controls exponential retry delays, maximum consecutive attempts
(zero means unlimited), connection timeout, implicit stale timeout and pending writes.
The default `PendingWritePolicy.Suspend` retains dirty application values but prevents
their transmission after a failure. Reads/readback resume. Inspect the fresh PLC values,
reconcile the command as needed, then call the session's `ResumeWrites()`.
`PendingWritePolicy.Retry` opts into retransmitting pending desired values after recovery.
This policy also covers restarting a stopped session with an uncertain pending edit.
Suspension cannot roll back writes or packets already delivered before a failure.

For explicit triggers, a configured handled-trigger mirror seeds the detector after
reconnection. An established ACK/NACK whose delivery was interrupted is re-delivered
without re-executing its completed data transfer, while the PLC still holds that request.
Without a mirror, completed-request deduplication cannot survive a process restart;
use the mirror when this matters. Pending-write suspension also holds new trigger
execution until the consumer resumes writes.

## Implicit fresh-baseline extension

Regenerate the PLC routine and API together before using managed recovery. Wire protocol
3 retains its 24-byte header and adds these values in the valid/flags DINT at byte 20:

| Flag | Meaning | Byte 16 |
| --- | --- | --- |
| 0 | Legacy invalid bootstrap | Unused |
| 1 | Normal command/snapshot | Referenced baseline / accepted command |
| 2 | API resynchronization request | Positive random recovery nonce |
| 3 | PLC frozen resynchronization baseline | Echoed recovery nonce |

A new nonce causes the wrapper to snapshot current PLC values and freeze that baseline
without applying any request payload. The API ignores old snapshots until its own nonce
is echoed. The next normal command references that fresh sequence; normal ACKs then resume.
Repeated requests with the same nonce do not repeatedly reset the baseline. The merge
AOI remains type ignorant, with exactly the same three SINT array parameters and PLC-first
byte rule. The memory emulator implements this handshake too.

## Native process ownership

`OpenerAdapter` supervises a private .NET child hosting the native stack. Each native
process still starts OpENer once. Reconnection/failure creates a fresh child, so the
consuming application need not restart. Native ABI 2 adds a connection epoch and guarded
image publication; connect/disconnect and idle transitions invalidate queued images.
The managed worker detects epoch changes even if they happen between polling cycles.

The worker uses private stdio IPC and the consumer's runtimeconfig/deps files. Deploy
a framework-dependent Windows x64 application with `dotnet` available. Single-file and
self-contained deployment are not supported by this worker launcher. Clean shutdown
closes stdin and waits; a nonresponsive worker is terminated only through the exact
owned process/creation handle. No process-name discovery or shared-process termination
is used. A wedged native thread cannot be unloaded inside the consumer.

`OpenerAdapter.ProbeWorkerAsync()` starts the packaged child and loads ABI 2 without
opening EtherNet/IP ports. It is a deployment smoke test, not a network interoperability
test. Native/controller behavior still requires the acceptance in VALIDATION.md.

## Unified diagnostics

Raw `ExplicitSession`, hosted explicit/implicit sessions and `OpenerAdapter` expose
`SessionDiagnostics`. It implements `INotifyPropertyChanged` and a `Changed` event.
The snapshot reports state, successful data updates, failures, recovery attempts,
last communication, last successful update/data age, last/maximum cycle duration,
missed duration deadlines, last error, suspended writes and the last trigger result.
Polling a held trigger updates communication time without claiming a new data update.
Callbacks run on the updating thread and subscriber failures are isolated. Marshal UI
work to the UI dispatcher. Deadline counts describe observed software cycle duration;
they are not a real-time or network-jitter guarantee.

`samples/HostingDemo` build-generates three contract classes and runs two explicit
cadences plus one 40-byte implicit image against memory transports without a PLC.
