# LogixTagRelay

C# access to Logix PLC tags through generated, strongly typed contracts. Browse tags
and UDTs, generate C#/JSON and PLC imports, then synchronize through explicit messaging,
implicit I/O, or a local memory emulator.

**0.8.1-preview.1.** Local and emulator checks are available. Real PLC communication
and Studio 5000 import acceptance remain unverified; see [validation](docs/VALIDATION.md).

## Table of Contents

- [Install](#install)
- [Usage](#usage)
- [Examples](#examples)
- [Documentation](#documentation)
- [Maintainers](#maintainers)
- [Contributing](#contributing)
- [License](#license)

## Install

Libraries target `net8.0`. Build with .NET SDK 8 or later; the examples use runtime
roll-forward and have been checked on .NET 10.

```powershell
git clone --recurse-submodules https://github.com/votecoffee/LogixTagRelay.git
cd LogixTagRelay
```

Packages are built locally; this preview is not published on nuget.org.

| Package | Use |
| --- | --- |
| `LogixTagRelay` | Typed values, contract configuration and synchronization |
| `LogixTagRelay.Build` | MSBuild generation from saved contract projects |
| `LogixTagRelay.Design` | L5X browsing and C#/JSON/PLC import generation |
| `LogixTagRelay.Logix` | Explicit Logix driver using libplctag |
| `LogixTagRelay.Opener` | Implicit EtherNet/IP adapter on Windows x64 |
| `LogixTagRelay.Emulation` | Shared PLC memory, JavaScript scripts and emulator client |

The browser, emulator and explicit driver need no OpENer build. To build and verify
the complete solution and create all six NuGets, run `./scripts/verify.ps1`. Building the
OpENer DLL also requires Visual Studio 2022 C++ tools, Windows SDK and CMake. Use
`artifacts/packages` alongside nuget.org as the consumer's package sources.

## Usage

### Generate a contract

```powershell
dotnet run --project tools/LogixTagRelay.Browser -c Release
```

Open <http://127.0.0.1:5274> and load an L5X export. Select tags, entire UDTs or array
elements and choose their access mode:

| Mode | PLC updates | App edits |
| --- | --- | --- |
| `PlcToApp` | Refresh `Value` | Setter is read-only |
| `AppToPlc` | Available as `ObservedValue` | Changed commands are written |
| `Bidirectional` | Refresh `Value` when no local edit is pending | Changed members are written |

A bidirectional UDT can contain read-only PLC members. The generator preserves those
permissions and makes its writable members bidirectional. Supported types include
BOOL, SINT, INT, DINT, LINT, REAL, USINT, UINT, UDINT, ULINT, DT, TIME, LREAL, TIME32, LTIME, LDT,
standard/custom STRING, nested UDTs, TIMER/COUNTER members and tag arrays.

The download includes ordinary C# source and `logixrelay.json`. Implicit generation
also includes the synchronization routine, SINT merge AOI and module settings. A
matching native module template can supply the module import.

Saved `.ltrx.json` projects retain catalog inputs, selections and settings. The
`LogixTagRelay.Build` package generates C# during compilation, with `LTR001` diagnostics.
See [contract projects](docs/CONTRACT_PROJECTS.md) and `samples/ContractProjectDemo`.

### Host multiple contracts

Use `RelayHost` for multiple explicit groups alongside one implicit group, capped at
480 bytes per image. Sessions expose shared diagnostics, retry transient faults with
backoff, and suspend uncertain pending writes until the consumer resumes them.
OpENer runs in an owned worker process so reconnecting does not restart the application.
See [hosting and recovery](docs/HOSTING_AND_RECOVERY.md) and `samples/HostingDemo`.

### Heartbeat and communication faults

Configure `Heartbeat = new HeartbeatOptions("ApiAlive")` for a five-second explicit BOOL
toggle, including for implicit-only hosting. The heartbeat stays outside the I/O image.
Typed connection/read/write exceptions and diagnostics expose failure phase, tag, native
status, retry delay, skipped intervals and queued overlapping requests. See
[liveness and errors](docs/LIVENESS_AND_ERRORS.md) and `samples/LivenessDemo`.

### React to tag changes

Generated contracts and each `PlcVariable<T>` implement `INotifyPropertyChanged`.
Subscribe to the contract's `TagValueChanged` for the PLC path, old/new typed values
and `Application`/`Plc` origin:

```csharp
tags.TagValueChanged += (_, change) =>
    Console.WriteLine($"{change.Path}: {change.OldValue} -> {change.NewValue}");
tags.Tag_Machine.Tag_Setpoint.PropertyChanged += (_, change) =>
{
    if (change.PropertyName == "Value") UpdateDisplay();
};
```

Identical reads and write acknowledgements do not produce value events. Array element
values notify in the same way. The contract's `Variables` collection implements
`INotifyCollectionChanged`; its membership is fixed, so element value changes use
property notifications. Callbacks run on the updating thread; marshal UI work to the
UI dispatcher. See [notification semantics and time types](docs/TYPES_AND_EVENTS.md).

### Read a UDT cyclically and write changed members

Add the generated C# to your application and keep its JSON alongside it. The
bidirectional example selects the whole `Machine` UDT:

```csharp
using LogixTagRelay;
using LogixTagRelay.Generated;
using LogixTagRelay.Logix;

var config = SyncConfiguration.FromJson(File.ReadAllText("logixrelay.json"));
var tags = new GeneratedTags();

await using var driver = new LogixTransport(config.Gateway, config.Route);
var session = new ExplicitSession(config, tags, driver);

await session.SynchronizeOnceAsync();          // Read the selected UDT.
tags.Tag_Machine.Tag_Setpoint.Value = 25f;      // Mark only Setpoint changed.
await session.RunAsync(cancellationToken);     // Cyclic reads; dirty-member writes.
```

Generated read groups let the Logix driver read each selected UDT as a root snapshot,
using controller-reported offsets to decode members. Writes address only the changed
member paths, such as `Machine.Setpoint`; the UDT buffer is never written back.
PLC changes to untouched members are therefore preserved. Pending local edits survive
the next read, and edits made during a write remain pending for the following cycle.

The generated classes work with IntelliSense, compiler checks and analyzers. They can
live in a separate contract library referenced by the consuming app. Use the same
model instance in your application and its synchronization session.

### Choose the update schedule

| Transport | Schedule | Completion |
| --- | --- | --- |
| Explicit, interval | Fixed cadence; default 100 ms | Individual member writes complete |
| Explicit, triggered | DINT changes to a nonzero value | Optional exact handled mirror and signed DINT ACK/NACK |
| Implicit | Cyclic full images; default 20 ms RPI | Matching sequence acknowledgement after PLC-first merge |

Implicit contracts support bidirectional members through the type-ignorant SINT AOI:
PLC bytes changed since the saved snapshot win conflicts. Explicit reads and writes
are separate operations; a pending app edit is written after the read. Neither mode
provides an atomic multi-tag PLC transaction. See [synchronization](docs/SYNCHRONIZATION.md).

## Examples

| Project | Demonstrates |
| --- | --- |
| `samples/BidirectionalDemo` | Whole-UDT read contract, PLC/app edits and changed-member-only writes |
| `samples/EmulationDemo` | Scripted position feedback over explicit, triggered explicit or implicit contracts |
| `samples/GeneratedDemo` | Generated nested UDT model and real-driver entry points |
| `samples/StringsAndArraysDemo` | Standard/custom STRING and 2D/3D arrays, including UDT arrays |

Run the bidirectional example without hardware:

```powershell
dotnet run --project samples/BidirectionalDemo -c Release
```

For the interactive emulator, run `tools/LogixTagRelay.Emulator`, open
<http://127.0.0.1:5275>, and click **Run script**. In another terminal:

```powershell
dotnet run --project samples/EmulationDemo -c Release -- explicit 25
dotnet run --project samples/EmulationDemo -c Release -- implicit 50
```

The emulator GUI edits PLC memory and displays API commands. JavaScript can automate
responses. It tests contract behavior over loopback HTTP; it does not execute PLC
programs or validate EtherNet/IP networking. See [emulator instructions](docs/EMULATION.md).

`samples/TypesAndEventsDemo` demonstrates unsigned/date/time values and change events
with both transports:

```powershell
dotnet run --project samples/TypesAndEventsDemo -c Release
dotnet run --project samples/TypesAndEventsDemo -c Release -- --implicit
```

## Documentation

- [Synchronization and explicit UDT reads](docs/SYNCHRONIZATION.md)
- [PLC module setup and imports](docs/PLC_SETUP.md)
- [Emulator and JavaScript scripts](docs/EMULATION.md)
- [Wire format and architecture](docs/ARCHITECTURE.md)
- [SINT merge AOI](plc/README.md)
- [Release verification, symbols and API compatibility](docs/RELEASING.md)
- [Validation and remaining acceptance](docs/VALIDATION.md)

## Maintainers

[@votecoffee](https://github.com/votecoffee).

## Contributing

Keep controller exports, machine-specific files and scratch data in ignored `.local/`.
Run `./scripts/verify.ps1` before proposing changes. Preserve the pinned OpENer source
and distinguish offline tests from native import and controller acceptance.

## License

[MIT](LICENSE). Dependencies have their own terms; see [third party notices](THIRD_PARTY_NOTICES.md).
