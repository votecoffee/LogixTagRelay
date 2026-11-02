# LogixTagRelay

Typed C# access to Logix PLC tags, with a tag browser, generated models/configuration,
explicit synchronization, and an experimental OpENer implicit I/O adapter.

**Status: 0.2.0-preview.1.** The managed code and Windows native bridge build locally.
Offline tests and the generated consumer run. PLC communication, native L5X import,
RPI behavior and controller acceptance have not been tested. See [validation](docs/VALIDATION.md).

## Table of Contents

- [Install](#install)
- [Usage](#usage)
- [Design and limits](#design-and-limits)
- [Maintainers](#maintainers)
- [Contributing](#contributing)
- [License](#license)

## Install

Build prerequisites: .NET SDK 8 or later. Windows x64, Visual Studio 2022 C++ build tools,
Windows SDK, CMake and Git are required to build the OpENer DLL. The native bridge
requires the MSVC x64 runtime. Use a supported .NET host; .NET 10 is the recommended
host for a new consumer. Libraries target `net8.0` for compatibility with .NET 8/9/10.
Tool/sample runtime configs use `LatestMajor` roll-forward; local checks ran on .NET 10.

```powershell
git clone --recurse-submodules https://github.com/votecoffee/LogixTagRelay.git
cd LogixTagRelay
.\scripts\build-native.ps1
dotnet build -c Release
dotnet run --project tests/LogixTagRelay.Tests -c Release
dotnet run --project samples/GeneratedDemo -c Release
dotnet run --project samples/StringsAndArraysDemo -c Release
.\scripts\pack.ps1
```

Packages are produced locally; public NuGet availability is not implied.

| Package | Purpose |
| --- | --- |
| `LogixTagRelay` | Typed variables, ownership, configuration, explicit scheduler, implicit image codec |
| `LogixTagRelay.Design` | Offline L5X catalog and C#/JSON/routine-L5X generation |
| `LogixTagRelay.Logix` | Explicit driver and online symbol/UDT discovery using libplctag 1.5.2 |
| `LogixTagRelay.Opener` | Windows x64 native adapter and its managed wrapper |

Use `artifacts/packages` as a local NuGet feed alongside nuget.org. Include the core,
Logix and/or Opener packages in the consuming application's restore. The Opener
package carries its DLL under `runtimes/win-x64/native` with its native license.

## Usage

### Browse and generate

```powershell
dotnet run --project tools/LogixTagRelay.Browser -c Release
```

Open <http://127.0.0.1:5274>. Load `samples/Demo.L5X` to try the entire workflow offline.
Select a root UDT, an array, an array element or individual members. Choose PLC → app
for observed data and app → PLC for commands. A parent selection includes its leaves.
The download contains:

- `GeneratedTags.cs`: nested classes, typed variables and arrays.
- `logixrelay.json`: field paths, directions, offsets, schema hash and transport settings.
- `RelayRoutine.L5X`: generated routine and program-local staging tags, when implicit files are selected.
- `ModuleSetup.txt`: the Generic Ethernet Module assembly numbers, lengths and commissioning steps.
- `GenericEthernetModule.L5X` and source-hash provenance when a matching native module template is supplied.

The browser defaults to a 480-byte image in both directions. Enter 0 to use the
smallest complete image, or another aligned size large enough for all selected fields.
Both directions use the same field offsets and include the entire contract, rather
than separate status-only and command-only layouts.

Load `samples/StringsAndArrays.L5X` to try standard STRING, custom strings, rectangular
2D/3D arrays, arrays of strings and arrays of UDTs. For the 480-byte demo contract,
select Metrics, Values, Cubes and Stations as PLC → app, and Request as app → PLC.
Arrays inside a Logix UDT are limited to one dimension; the generator enforces this
native restriction. Multidimensional arrays are controller/program tags, including
tags whose element type is a UDT or a custom string.

For module generation, inspect a native Data - SINT module export with no separate
status connection. It must match the configured assemblies (defaults: input 101,
output 100, configuration 102), equal input/output sizes, and configuration size zero.
Generation resizes known I/O metadata and initial data together, updates name/address/RPI,
and preserves opaque CommMethod/configuration data and parent routing. A mismatched
template is rejected with specific corrections. A configuration tag may retain native
placeholder storage even when its active configuration payload size is zero.

Online browsing is a read-only operation initiated with the address and route entered
in the browser. It reads symbols and templates; it does not write tags. Online discovery
does not establish write permissions, so online-only catalogs permit PLC → app generation.
Load an L5X with access metadata for writable mappings. The online parser is implemented
but still needs controller interoperability checks. A successful offline catalog test
does not establish successful online browsing on a controller.

### Consume a generated model

Add the generated C# file to the consumer and copy the JSON to its output directory.
Keep them together; sessions reject mismatched model/configuration fields.

```csharp
using LogixTagRelay;
using LogixTagRelay.Generated;
using LogixTagRelay.Logix;

var config = SyncConfiguration.FromJson(File.ReadAllText("logixrelay.json"));
var tags = new GeneratedTags();

tags.Tag_Command.Tag_Enable.Value = true;
tags.Tag_Command.Tag_Setpoint.Value = 125.5f;

await using var driver = new LogixTransport(config.Gateway, config.Route);
var session = new ExplicitSession(config, tags, driver);
await session.RunAsync(cancellationToken);
```

`RunAsync` is awaited for the session lifetime. In an application, run it as an owned
background task, use the same `tags` instance from the consumer, cancel and await it
on shutdown, then dispose the transport. Each variable exposes `Value`, `Quality`,
`Timestamp`, `Dirty` and `Version`. PLC-owned setters reject app writes. App-owned
values remain authoritative; their PLC acceptance is tracked separately.
Changes made while a write is in flight remain dirty for the following update.
`ObservedValue`, `HasObservation` and `PlcTimestamp` expose typed PLC readback separately
from the desired app-owned value. Explicit mode reads all contract fields before
flushing dirty commands. Implicit mode receives all fields in one complete image.

STRING properties use `PlcVariable<string>`. Standard STRING has capacity 82; custom
capacity is read from the declared DINT LEN / SINT DATA[N] definition. Values use a
lossless byte-to-character Latin-1 mapping, including embedded nulls and bytes 128..255.
Characters above U+00FF and over-capacity values are rejected; no truncation or character
replacement occurs. Explicit native string transfers preserve trailing native padding.

```csharp
tags.Tag_Request.Tag_Recipe.Value = "Recipe A"; // custom capacity 128 in the extended demo
float value = tags.Tag_Values[1, 2].Value;
long count = tags.Tag_Cubes[1, 1, 1].Value;
string name = tags.Tag_Stations[1, 1].Tag_Name.Value;
```

Explicit mode polls at a fixed cadence by default. For a larger triggered group:

```csharp
config = config with
{
    Explicit = new ExplicitOptions(
        TriggerTag: "RequestSequence", TriggerPollMs: 20,
        AcknowledgeTag: "AckSequence")
};
```

Request and acknowledgement are separate DINT sequence tags. The first observed request
causes an update, including zero; subsequent changes cause another update. The PLC
must hold request data stable and wait for the echoed acknowledgement before issuing
the next request. Short BOOL pulses are not supported by this preview. The timer
coalesces overruns and never overlaps cycles. A failure faults the session; there is
no automatic replay after an indeterminate write. Read/write groups are not atomic
PLC transactions and a failed group can contain partial successful writes. Restarting
a triggered session can repeat work; the handshake is not durable exactly-once execution.

### Implicit I/O

The PLC is the scanner/originator; the consuming PC runs the OpENer adapter/target.
Assign **every** app-owned value before starting so implicit startup cannot publish
uninitialized commands.

```csharp
using LogixTagRelay.Opener;

await using var adapter = new OpenerAdapter(config, tags);
await adapter.RunAsync("Ethernet 2", deviceSerialNumber: 12345678, cancellationToken);
```

Use the actual NIC name/index and a unique device serial number. This call binds
EtherNet/IP ports; construction and the offline sample do not start the network stack.
Cancel and await `RunAsync` before disposal. The bridge supports one start per process;
restart the host after failure/disconnection. It uses the selected NIC's existing
configuration and does not change NIC addresses or firewall rules.

For the extended STRING/array example, configure a Generic Ethernet Module with:

| PLC setting | Value | Direction |
| --- | --- | --- |
| Name | `LogixRelay` | Referenced by generated logic |
| Address | PC adapter NIC IPv4 address | The PC, rather than the PLC address |
| Comm Format | Data - SINT | 8-bit data elements |
| Input assembly / size | 101 / 480 | PC → PLC |
| Output assembly / size | 100 / 480 | PLC → PC |
| Configuration assembly / size | 102 / 0 | No configuration payload |
| Separate status connection | Disabled | Status is carried in the application header and PLC module diagnostics |
| Requested packet interval | 20 ms | Initial setting, still requires bench testing |

Import `samples/StringsAndArraysDemo/RelayRoutine.L5X` into the applicable program and call
`LogixRelay_PS_Sync` from a scheduled task. Existing application tags must already
exist. Supply a matching native module export to generate a module import copy, or add
the module with the supplied settings. Generated module and routine files still require
Studio 5000 import, verification and re-export/readback; structural checks do not prove
native acceptance. The smaller original Demo contract uses 60 bytes in both directions.

Drive `LogixRelay_PS_ApplyCommands` from your application permission logic. It defaults
false. The generated 1000 ms watchdog blocks application of stale command frames.
The application must use `LogixRelay_PS_Watchdog.DN` and module status to choose the
appropriate hold/reset behavior for commands already copied into its tags.

The adapter echoes the whole received PLC image and overlays app-owned fields. It
waits for the command sequence acknowledgement before publishing another command
generation; PLC-owned echo bytes continue refreshing while that acknowledgement is
pending. The PLC applies only app-owned fields and publishes every field back,
including command readback. This is cyclic I/O with application-level
acknowledgements, not real-time C# execution. A 20 ms RPI does not guarantee a 20 ms
app/PLC round trip. The native sender may repeat the latest complete image while
the managed app is delayed; only a new sequence refreshes the PLC watchdog.

## Design and limits

See [architecture](docs/ARCHITECTURE.md) for the byte contract and engineering decisions.

- Supported values: BOOL, SINT, INT, DINT, LINT, REAL, standard/custom STRING, nested UDTs,
  and 1D/2D/3D tag arrays. String capacities range from 1 to 65535 bytes. Array expansion
  is bounded to 20000 catalog nodes. UDT member arrays follow Logix's one-dimension limit.
- TIMER/COUNTER application members, alias resolution and safety-tag workflows are not
  supported for mapping in this preview.
- One owner per field. For a UDT containing status and commands, select different
  members with different directions. Whole-UDT selection assigns one direction to all leaves.
- Implicit groups are capped at 480 payload bytes per direction, including a 24-byte header.
  Both images include all fields, so the combined contract must fit. Split larger selections into groups; multiple native adapter instances in one process
  are not supported yet. Explicit-only generation does not impose the I/O payload cap.
- PLC native UDT padding/BOOL packing is never inferred from CLR layout. Generated
  field copies use explicit wire offsets. No `Marshal.StructureToPtr` or arbitrary
  raw UDT memory copies are used.
- PLC snapshots across multiple leaf copies are not globally atomic. Use dedicated
  PLC-owned snapshot/command tags if application-level consistency requires it.
- Generated routine imports are structurally checked, not certified native-valid.
  Import/verify/re-export/readback and controller testing remain necessary.
- The pinned OpENer WIN32 code emits upstream MSVC warnings, including socket-width
  conversions on x64. Native build success is not a claim of production readiness.
- There is no automatic schema re-browse, automatic native project import, reconnect/replay,
  distributed conflict resolution, durable delivery journal or public NuGet release yet.
- Model/config hash checks do not verify the running PLC's type definitions. Refresh
  the catalog after PLC changes; live type-contract validation remains to be added.
- Version 0.2 uses JSON schema/wire protocol 2. Regenerate C#, JSON and PLC routine
  together when upgrading from 0.1; old directional images are deliberately rejected.

## Maintainers

[@votecoffee](https://github.com/votecoffee).

## Contributing

Keep controller/project data and machine-specific captures in ignored `.local/` storage.
Run the offline checks and generated consumer before proposing changes. Preserve failed
native receipts and distinguish build, offline, import and controller evidence. Keep
OpENer pinned and unmodified unless a dependency change is deliberate and separately reviewed.

## License

[MIT](LICENSE) for this repository's code. OpENer and libplctag have their own terms;
see [third party notices](THIRD_PARTY_NOTICES.md) and the retained dependency licenses.
