# Tag synchronization

## Access modes and local edits

`Direction.Bidirectional` makes the normal typed `Value` follow PLC observations when
the variable is clean. Assigning a different value creates a pending local edit.
Reads continue to update `ObservedValue` and `PlcTimestamp`, while retaining that edit
until it is written or an implicit merge completes. A PLC-only member rejects setters.
`AppToPlc` remains available for command values whose desired value stays separate from
readback; `PlcToApp` remains observation-only.

Selecting an entire UDT as bidirectional preserves read-only leaves as `PlcToApp` and
makes externally writable leaves bidirectional. Unknown write permissions are not
treated as permission to write. Online-only catalogs therefore need a matching export
before writable contracts can be generated.

Only changed values become dirty. Assigning the current value again after observation
does not queue an extra write. A first assignment before any observation is intentional,
even if it equals the CLR default. An edit reverted to the freshly read PLC value is
accepted without a network write. Changes made while a prior version is being written
remain dirty; acknowledging the captured version cannot discard the newer edit.

`Dirty` describes pending work, not a guarantee that PLC application logic consumed it.
`Quality` is Unknown initially, Good after a successful observation/transfer and Bad
after a failed or stale group. Timestamps are PC observations rather than PLC clock values.

## Explicit UDT reads and member writes

The generator records selected UDT roots under `explicit.readGroups`. Each entry has a
symbolic root path, expected PLC data type and the selected member paths decoded from it.
UDT arrays are grouped per selected element; scalar fields outside a group are read
individually. Read groups can cover a subset of members without changing the write map.

For example, selecting `Machine` produces a group for `Machine`, with mappings such as
`Machine.Setpoint`, `Machine.Counts[2]` and `Machine.Axis.Position`. The cycle is:

1. Read each UDT root and any uncovered scalar members.
2. Decode and validate all results before publishing observations.
3. Update clean bidirectional values and retain pending local edits.
4. Write only dirty writable members whose desired value differs from the fresh read.
5. Acknowledge only the versions captured for those writes.

There is no whole-UDT write operation. If the PLC changes `Machine.Counts[2]` while the
app changes `Machine.Setpoint`, the next cycle observes the count and writes only the
setpoint. `samples/BidirectionalDemo` executes this case without hardware and reports
the exact member write paths.

`LogixTransport` reads `@tags`/`@udt` metadata when first resolving a group, verifies the
expected type, and decodes the native root buffer using reported offsets, sizes, array
counts and packed BOOL bit locations. It handles nested UDTs, primitive member arrays
and counted STRING layouts. Wire offsets and CLR object layout are not used as native
UDT offsets. A root read can require multiple CIP fragments; it is not a promise of a
single packet or a scan-atomic controller snapshot.
The native descriptor format is documented in libplctag's
[`@udt` interface](https://github.com/libplctag/libplctag/wiki/Special-Tags) and
[descriptor example](https://github.com/libplctag/libplctag/blob/release/src/tools/list_tags_logix/list_tags_logix.c).

Native template plans are cached for the transport lifetime. Refresh the export and
recreate the transport after controller type changes; changed buffer sizes and decoded
type/capacity mismatches fail before writes. No automatic live schema migration is
implemented. Native/controller interoperability for grouped reads is still pending.

One-dimensional BOOL array elements use individual masked bit addresses, not a write
of their containing 32-bit word. They are excluded from generated UDT decode groups
and read individually. Multidimensional BOOL array addressing is rejected by this
explicit driver; other supported tag-array types retain 1D/2D/3D addressing.

Custom transports can implement `IExplicitTransport.ReadBatchAsync` to use the same
read plan. Its default implementation reads members separately for compatibility.
The memory emulator implements a single memory snapshot; the HTTP client retrieves
that snapshot from the shared emulator process. To disable native root reads for a
particular configuration, set `Explicit.ReadGroups` to an empty list before creating
the session, retaining the member-by-member read fallback.

Explicit mode is read-then-write. A pending application value is written even if that
same PLC member changed before the read. A PLC edit occurring between that read and
write can be overwritten; CIP does not provide an atomic compare-and-swap here. Use
application interlocks or the implicit PLC-first merge when that conflict policy is
required. Multiple member writes can partially succeed before a fault.

## Interval and DINT-triggered explicit groups

With no `TriggerTag`, `ExplicitSession.RunAsync` runs immediately and then at
`IntervalMs` (100 ms by default), without overlapping cycles. For triggered groups:

```csharp
config = config with { Explicit = config.Explicit! with
{
    TriggerTag = "RequestSequence",
    HandledTriggerTag = "HandledSequence", // optional exact DINT mirror
    ResponseTag = "ResponseSequence"       // optional signed DINT ACK/NACK
}};
```

Each observed change to a nonzero DINT runs a group. Zero is idle and re-arms the same
following nonzero value. The optional response becomes zero before work, positive
request magnitude for ACK or negative magnitude for NACK, followed by the exact handled
mirror. Use positive counters and roll over to 1 at a suitable threshold, such as
200000000. The minimum DINT cannot produce a positive DINT ACK and is rejected before
data work when signed replies are enabled. `AcknowledgeTag` is the legacy alias for
the handled mirror.

Control tags are separate DINTs outside the data mapping. Hold requests until handled;
polling cannot detect changes that happen entirely between polls. NACK means the group
failed, not that earlier successful writes were rolled back. Notification failure also
faults the session. `LastTriggerResult` distinguishes the data result from notification
delivery. There is no durable exactly-once guarantee or automatic replay after faults.

## Implicit round trips

Both SINT assemblies carry the complete contract at the same offsets. The app echoes
the last PLC snapshot, overlays dirty writable members and returns its baseline
sequence. Startup frames are invalid until the first PLC baseline is received.

The generated wrapper packs live PLC values, protects PLC-only fields and calls the
type-ignorant Structured Text AOI on LastSent/Current/Received payload arrays. For each
byte it accepts the API byte only if the current PLC byte still equals LastSent. This
applies equally to writable command and bidirectional members; changed PLC bytes win.
Resolution is per byte and can combine bytes within a multi-byte value. Incoming and
merged STRING LENs are checked before any command destination is copied.

After a completed merge the PLC publishes actual tag readbacks and the command ACK,
then saves that output payload as the next baseline. The API adopts the merged value
unless a newer local edit is pending. Duplicate packets do not reapply commands.
Application-level DINT triggers are an explicit scheduling feature; implicit transport
uses its cyclic sequence handshake. See [PLC setup](PLC_SETUP.md) for integration.
