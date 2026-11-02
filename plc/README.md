# PLC helpers

`LTR_SintMerge.L5X` is an authored Structured Text Add-On Instruction import.
Its source is `LTR_SintMerge.st`, embedded in the Design package so generated imports
can carry the same definition. Native import/verification is still required.

The AOI has exactly three `InOut` data parameters, all declared `SINT[1]`:

| Parameter | Meaning | Modified by the AOI |
| --- | --- | --- |
| `LastSent` | Snapshot saved when the caller wrote its module output | No |
| `Current` | Current PLC bytes; becomes the merged result | Yes |
| `Received` | New bytes received from the peer | No |

The caller must provide three distinct arrays of equal actual size. The AOI obtains
their lengths with `SIZE` and returns `Error=1`, `Status=1` without changing `Current`
if their sizes differ. Its declaration is a minimum bound, not a fixed payload size.
See [Rockwell's AOI manual, single-dimension InOut arrays](https://literature.rockwellautomation.com/idc/groups/literature/documents/pm/1756-pm010_-en-p.pdf).

For each byte, it copies `Received[i]` into `Current[i]` only when
`Current[i] = LastSent[i]`. A PLC change therefore always wins, including when both
peers changed the same byte. It does not know fields, UDTs, strings, byte order or
ownership. Resolution is per byte, including bytes belonging to larger values.

Create an instance tag of type `LTR_SintMerge` and call it from Structured Text:

```text
LTR_SintMerge(MergeInstance, LastSent, Current, Received);
```

Outputs include `PlcChangedBytes`, `ReceivedChangedBytes`, `ConflictBytes` and
`AppliedBytes`, reset on each executed call. `LastSent` is maintained by the caller,
after its output write. Do not update the baseline on reception or inside the AOI.
Use only snapshots known to belong to the same exchange; equal array sizes alone
do not establish matching baselines.

Offline tests execute the emitted ST with a bounded subset interpreter and compare
it against independent expected merge results. That establishes source/model behavior,
not Studio 5000 verification, scan timing or controller acceptance.

## Generated wrapper

Each implicit contract routine embeds this AOI and creates one instance plus payload
arrays named `<module>_PS_LastSent`, `_Current` and `_Received`. The 24-byte protocol
header is excluded from these arrays. Tag-to-byte and byte-to-tag CPS instructions
remain in the generated ladder wrapper; the AOI receives no type information.

The wrapper writes a snapshot to module output and immediately saves its payload.
It freezes that snapshot until the API returns a fresh command referencing that exact
PLC sequence. It packs live PLC values, stages the API payload, protects PLC-only
slots, calls the AOI, then copies merged writable leaves back to the selected tags.
Incoming and merged STRING LENs must be valid before any destination copy occurs.
It acknowledges only after those copies and publishes the next matching baseline.
This handshake prevents a scan from silently replacing LastSent with a newer snapshot
while the API is still replying to an older one.

Only one task should own the module output and staging arrays. Other tasks can still
change application tags between individual copies; use application interlocks or
dedicated staging tags when a coherent multi-tag snapshot is required.
