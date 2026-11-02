# PLC setup and generated imports

## Tag selection and generated files

The browser at <http://127.0.0.1:5274> accepts a controller L5X or read-only online
discovery. Offline exports supply external access metadata needed for writable modes.
Selections can be whole UDTs, tag arrays, array elements or individual nested members.
Keep the generated C# and JSON together; sessions reject mismatched field mappings.

Supported values are BOOL, SINT, INT, DINT, LINT, REAL, LREAL, USINT, UINT, UDINT, ULINT, DT, TIME, TIME32, LTIME, LDT, standard/custom counted STRING
and nested UDTs. String capacity comes from LEN/DATA metadata (1..65535 bytes). Text
maps bytes losslessly to Latin-1, including embedded nulls; invalid lengths and
characters above U+00FF are rejected. Native string tail padding is preserved on
explicit member writes. Classic TIMER/COUNTER members are supported with read-only instruction status flags. Aliases and safety tags
are not supported in this preview.

Tag arrays may have one, two or three dimensions; native UDT member arrays have one.
Generated models use C# rectangular arrays and Logix comma subscript paths. The
explicit driver's BOOL array addressing supports one dimension; see
[synchronization](SYNCHRONIZATION.md). Catalog expansion is bounded to 20000 nodes.

Implicit output includes `RelayRoutine.L5X`, `LTR_SintMerge.L5X`, its `.st` source and
`ModuleSetup.txt`. The routine embeds its AOI dependency. A matching native module
template optionally supplies `GenericEthernetModule.L5X` plus a source-hash receipt.
Selected application tags must already exist in the PLC; imports create staging
tags and the helper AOI. Read-only selected members are never written back.

## Generic Ethernet Module

The PLC is the scanner; the PC runs the OpENer adapter. Defaults, from the PLC's view:

| Setting | Value |
| --- | --- |
| Address | IPv4 address of the PC adapter NIC |
| Comm Format | Data - SINT |
| Input assembly | 101, PC → PLC |
| Output assembly | 100, PLC → PC |
| Input/output lengths | Equal, matching the complete contract image |
| Configuration | Assembly 102, size zero |
| Separate status connection | Disabled |
| RPI | 20 ms initial setting; validate on the intended controller/network |

Each image includes a 24-byte application header. Both directions use identical field
offsets and include every selected leaf. The native bridge caps each image at 480
bytes, including the header. Split larger implicit selections into separate contracts;
explicit-only contracts do not have that assembly limit. Enter 0 in the browser to
choose the minimum aligned size, or select a fixed aligned image length.

A module template must already have the matching assemblies, configuration size zero,
equal I/O lengths and no separate status connection. Generation resizes known sizes
and initial data together and updates name/address/RPI while preserving opaque native
mode/configuration data and parent routing. Mismatches require a corrected native
export. XML structural checks do not establish Studio 5000 acceptance.

## Routine integration and lifecycle

Import the routine into the program containing any selected program-scoped tags; one
program scope is supported per group. Call `<module>_PS_Sync` from a scheduled task.
Give the generated staging prefix its own namespace and one task owner.

Drive `<module>_PS_ApplyCommands` from application permission logic; it starts false.
The 1000 ms watchdog diagnoses a lack of fresh matching exchanges. Repeated packets
cannot refresh it; fresh matching generations can. Application logic chooses the
hold/reset behavior for prior commands and module faults. Hold `<module>_PS_Reset`
during reconciliation, release it, then restart the API session.

The PLC output snapshot remains frozen until the API returns a matching fresh reply.
The wrapper saves LastSent immediately after writing module output. Other PLC tasks
can still change application tags between individual copies; use dedicated snapshot
tags or application interlocks for consistent multi-tag process data.

`OpenerAdapter.RunAsync` takes the PC NIC name/index and a unique device serial. It
binds EtherNet/IP ports and uses the NIC's existing settings. Cancel and await it before
disposal. This bridge supports one start per process; restart after failure or
disconnection. It does not configure NIC addresses, firewall rules or reconnect/replay.

Version 0.5 retains JSON schema 2 / implicit wire protocol 3 and adds bidirectional
permissions and explicit UDT read groups. Update the consuming library when using
new generated JSON. Changing a field's direction changes the mapping hash: regenerate
C#, JSON and the PLC routine together. Native import/verify/re-export and controller
bench testing remain required; see [validation](VALIDATION.md).
