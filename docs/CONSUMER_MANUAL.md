# From a contract download to a .NET application

Use an existing .NET 8 or later project. Install the reusable LogixTagRelay packages, then add the compiled contract library from **Download interface**. The C# examples start with an in-memory test and then show explicit and implicit connections.

## 1. Install the NuGet packages in Visual Studio

The NuGets contain the software that runs the contract. Your contract contains the generated tag types and configuration for the PLC tags you selected. The interface ZIP ships your contract as `LogixTagRelay.Contracts.dll`.

Add a local package source once, then install the packages through **Manage NuGet Packages** in the consuming project. Use the version listed in the ZIP's `README.txt` (`0.8.1-preview.1` for the current preview). [Set up a local NuGet feed](LOCAL_NUGET_FEED.md)

| Package | Install when |
| --- | --- |
| `LogixTagRelay` | You reference the compiled contract library. It supplies the tag types, configuration and session APIs. |
| `LogixTagRelay.Logix` | You connect to a live PLC using explicit messaging. |
| `LogixTagRelay.Opener` | You connect using live implicit I/O on Windows x64. It includes the native bridge. |
| `LogixTagRelay.Emulation` | You run the example below or connect to the local PLC memory emulator. |

## 2. Download and reference the compiled contract

1. In the [Contract Editor](http://127.0.0.1:5274/), select tags and directions and save the contract. For the exact examples below, use an explicit contract with namespace `MyPlant.Contracts` and class `TelemetryTags`. Select a scalar DINT named `DemoCount` as **PLC ↔ app** and leave the trigger and heartbeat blank. For your own contract, substitute its namespace, class and tag names.
2. Choose **Download interface**. The editor compiles the workspace into one .NET 8 library before downloading. Extract the ZIP to a stable folder in or beside your solution.
3. In Visual Studio, right-click the app's **Dependencies**, choose **Add Project Reference**, select **Browse**, and pick `dist/LogixTagRelay.Contracts.dll`. Keep the XML documentation next to the DLL for IntelliSense. Build your app. `TelemetryTags.CreateConfiguration()` returns the matching configuration directly from the assembly.

| Where | What you use it for |
| --- | --- |
| `dist/` | The prebuilt contract DLL and IntelliSense XML. |
| `contracts/<contract-folder>/` | The editable `.ltrx.json`, generated C# and `logixrelay.json` for one contract. The emulator loads the per-contract `logixrelay.json`. |
| `plc/` | The implicit routine, merge AOI, module settings and optional module import. |
| `LogixTagRelay.workspace.json` | Reopen the full project in the Contract Editor. |
| `Build Contracts.cmd` | Rebuild the DLL from the included C# using the adjacent project file and the configured NuGet source. |

## 3. Run a read and write without a PLC

Use this in a console app that references the contract DLL. The memory store plays the PLC side of the contract for this test. `DemoCount` is the PLC path and `Tag_DemoCount` is the generated C# property.

```csharp
using LogixTagRelay;
using LogixTagRelay.Emulation;
using MyPlant.Contracts;

var tags = new TelemetryTags();
var config = TelemetryTags.CreateConfiguration();
var memory = new PlcMemory(config);
await using var transport = new MemoryExplicitTransport(memory);
var session = new ExplicitSession(config, tags, transport);

memory.Set("DemoCount", 41);
await session.SynchronizeOnceAsync();
Console.WriteLine($"PLC to app: {tags.Tag_DemoCount.Value}");

tags.Tag_DemoCount.Value = 42;
await session.SynchronizeOnceAsync();
Console.WriteLine($"App to PLC: {memory.Read("DemoCount")}");
```

Expect `PLC to app: 41` and `App to PLC: 42`. To try PLC-side changes from a GUI or script, load the same `logixrelay.json` in the emulator. The [simulated I/O manual](SIMULATED_IO_MANUAL.md) walks through JavaScript responses and the C# client for both explicit and implicit contracts.

## 4. Run the explicit contract against the PLC

Check the controller address and CIP route in the saved contract. Use a test controller or tags that your application is authorized to access. For a read-only check, select PLC → app directions and leave the heartbeat and trigger reply tags blank. A configured heartbeat or trigger reply can write independently of assignments to a data tag.

Replace the in-memory example with this live example. The app logs changes and faults. Assign a writable `Value` from your own application logic when you want to submit a command.

```csharp
using LogixTagRelay;
using LogixTagRelay.Logix;
using MyPlant.Contracts;

var tags = new TelemetryTags();
var config = TelemetryTags.CreateConfiguration();
var recovery = new RecoveryOptions(InitialDelayMs: 1000, MaximumDelayMs: 5000);
var host = new RelayHost();

var telemetry = host.AddExplicit("telemetry", config, tags,
    _ => ValueTask.FromResult<IExplicitTransport>(new LogixTransport(config.Gateway, config.Route)),
    recovery);

tags.Tag_DemoCount.PropertyChanged += (_, change) =>
{
    if (change.PropertyName == nameof(tags.Tag_DemoCount.Value))
        Console.WriteLine($"DemoCount = {tags.Tag_DemoCount.Value}");
};
telemetry.Diagnostics.FaultOccurred += (_, fault) =>
    Console.Error.WriteLine($"{fault.Operation} {fault.Kind} {fault.TagPath}");

using var stop = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Cancel(); };

// Register optional implicit I/O here.
await host.RunAsync(stop.Token);
```

Run the console app and press **Ctrl+C** to stop. The live session follows the contract's timer or DINT trigger settings. For a triggered group, the PLC requests an update by changing the DINT to a new nonzero value. The optional handled mirror echoes the trigger. A configured signed DINT reply uses a positive ACK and negative NACK. Inspect `telemetry.Diagnostics.Snapshot` for missed intervals and recovery state. In a desktop UI, marshal tag change callbacks to the UI thread.

For a command-only tag, `Value` holds the requested command and `ObservedValue` holds the PLC readback. Repeat `AddExplicit` with a new generated model and a unique session name for each explicit contract. Give each write/control tag a single owner on the same controller and route.

## 5. Add the implicit contract to the same app

1. Add the implicit contract to the same editor workspace and download the interface again. Reference the updated DLL. This example assumes that its C# class is `IoTags` in `MyPlant.Contracts`.
2. In Studio 5000, import the supplied `GenericEthernetModule.L5X` if needed, or configure the existing module using `ModuleSetup.txt`. Use the PC NIC address, matching SINT lengths in both directions, and the generated assembly instances and RPI. Each direction includes the relay header and has a 480-byte maximum.
3. Import `RelayRoutine.L5X` into the scheduled program where you intend to call it. Verify that the selected application tags already exist, then add the JSR for `<module>_PS_Sync` to that program. Use the generated `<module>_PS_ApplyCommands` bit under your PLC's command permission logic. See [PLC setup and imports](PLC_SETUP.md) for the staging and watchdog behavior.
4. Install `LogixTagRelay.Opener` through **Manage NuGet Packages** and add `using LogixTagRelay.Opener;` to `Program.cs`. Build and run the app as a Windows x64 process. `Get-NetAdapter` lists the NIC names. Use the NIC whose IPv4 address is configured on the PLC module and make sure the demo or another adapter instance has released the EtherNet/IP ports.
5. Replace the optional I/O comment in step 4 with the code below. Substitute your PC NIC name and a device serial number unique on that network. Keep `await host.RunAsync(stop.Token);` after the registration so cancellation finishes both sessions before disposal.

```csharp
var ioTags = new IoTags();
var ioConfig = IoTags.CreateConfiguration();
await using var io = new OpenerAdapter(ioConfig, ioTags,
    nicNameOrIndex: "Ethernet", deviceSerialNumber: 0x00001234, recovery: recovery);
host.Add("implicit-io", io);
```

The host supports multiple explicit groups and one implicit group. To run implicit alone, keep the host and cancellation code and register only `io`. If the implicit contract has a heartbeat, the adapter handles it through an explicit connection.

## 6. Refresh or rebuild a contract

To change tags or settings, edit and save in the Contract Editor, then download again and update the app's assembly reference to the new DLL. If the implicit mapping changed, use the `plc/` files from the same download and verify both peers together.

Use **Build Contracts.cmd** to recompile the included C# after configuring the local NuGet source. It writes to `dist/`. For automated generation from `.ltrx.json` during your own build, use the separate `LogixTagRelay.Build` package. [Build-time contract generation](CONTRACT_PROJECTS.md)

## Troubleshooting

| Symptom | What to check |
| --- | --- |
| Restore cannot find a relay package | Check that the pinned `.nupkg` is in the solution's `packages` directory and that restore uses the solution's `NuGet.Config`. |
| `LTR001` during build | Read the generator message for the specific contract. Check its catalog, selected paths and C# names. |
| Generated type is missing or duplicated | Check the namespace and class in the editor and the assembly reference in your app. Use distinct namespace and class combinations across contracts. |
| Explicit connection or tag read fails | Check the fault's operation, endpoint, route and tag path. Reachability, EtherNet/IP session registration and tag access are separate steps. |
| Implicit I/O connects but values remain stale | Check the scheduled JSR, module sizes and instances, the PLC watchdog and permission bit, and that the PLC and app use the same contract download. |

See [contract project details](CONTRACT_PROJECTS.md), [hosting and recovery](HOSTING_AND_RECOVERY.md), and [heartbeat and errors](LIVENESS_AND_ERRORS.md).
