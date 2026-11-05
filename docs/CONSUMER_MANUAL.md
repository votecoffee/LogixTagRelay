# From a contract download to a .NET application

This walkthrough builds a .NET 8 console app and a class library for the generated PLC contracts. Start with an in-memory round trip, then connect the app to an explicit PLC contract. If you downloaded an implicit contract, you can add it to the same app.

## 1. Download the project interface

1. In the [Contract Editor](http://127.0.0.1:5274/), add or edit a contract. Select the PLC tags and directions, then choose **Save contract**. Use an L5X catalog if you want tag and UDT member comments in IntelliSense.
2. For the exact example below, make an **explicit** contract with C# namespace `MyPlant.Contracts` and class `TelemetryTags`. Select a scalar DINT named `DemoCount` as **PLC ↔ app** and leave the DINT trigger and heartbeat blank. You can use your own contract by substituting its namespace, class, tag path and generated property in the examples.
3. On the contracts page, choose **Download interface**. Extract the ZIP. Keep each generated contract folder together.

| File | Use |
| --- | --- |
| `LogixTagRelay.workspace.json` | Reopen the entire project in the Contract Editor using **Open or import**. |
| `contracts/<contract-folder>/contract.ltrx.json` | Generate one typed C# model at build time. |
| `GeneratedTags.cs` and `logixrelay.json` | Compile the downloaded model directly, or use the JSON with the emulator. |
| `RelayRoutine.L5X`, `LTR_SintMerge.L5X`, `LTR_SintMerge.st` and `ModuleSetup.txt` | Set up the PLC side of the implicit contract. The routine includes its AOI dependency. |
| `GenericEthernetModule.L5X` and `ModuleProvenance.txt` | Import the module when a compatible Studio 5000 export was attached. The provenance file records the source export hash. |

## 2. Create the solution and configure NuGet

Install a .NET SDK that can build `net8.0` and the .NET 8 runtime. The example uses the `0.8.1-preview.1` packages from the demo's `packages` directory or the repository's `artifacts/packages` directory. For live implicit I/O, use Windows x64. The packaged adapter includes the native bridge.

From a parent directory where you want the solution, run this in PowerShell. Set `$interfaceZip` to the ZIP you downloaded and `$previewFeed` to the directory containing the preview `.nupkg` files.

```powershell
$interfaceZip = "$env:USERPROFILE\Downloads\LogixTagRelay.Interface.zip"
$previewFeed = "$env:USERPROFILE\Downloads\LogixTagRelay-Demo\packages"

New-Item -ItemType Directory RelayConsumer | Out-Null
Set-Location RelayConsumer
dotnet new sln --name RelayConsumer
dotnet new classlib --name RelayContracts --framework net8.0 --no-restore
dotnet new console --name RelayApp --framework net8.0 --no-restore
dotnet sln add .\RelayContracts\RelayContracts.csproj .\RelayApp\RelayApp.csproj
Remove-Item -LiteralPath .\RelayContracts\Class1.cs

Expand-Archive -LiteralPath $interfaceZip -DestinationPath .\Interface
New-Item -ItemType Directory .\packages, .\RelayContracts\Contracts | Out-Null
Get-ChildItem -LiteralPath $previewFeed -Filter '*.0.8.1-preview.1.nupkg' |
    Copy-Item -Destination .\packages
Get-ChildItem .\Interface\contracts -Directory | Select-Object Name
```

Create `NuGet.Config` in the solution directory. Source mapping sends the relay packages to your local folder and the third-party dependencies to nuget.org.

```xml
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
    <add key="relay-preview" value="packages" />
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
  </packageSources>
  <packageSourceMapping>
    <packageSource key="relay-preview">
      <package pattern="LogixTagRelay" />
      <package pattern="LogixTagRelay.*" />
    </packageSource>
    <packageSource key="nuget.org">
      <package pattern="*" />
    </packageSource>
  </packageSourceMapping>
</configuration>
```

## 3. Add the downloaded contract to the class library

Copy the `.ltrx.json` from the appropriate folder. If your ZIP lists a different folder name, substitute it in the command.

```powershell
Copy-Item -LiteralPath .\Interface\contracts\01-TelemetryTags\contract.ltrx.json `
    -Destination .\RelayContracts\Contracts\Telemetry.ltrx.json
```

Replace `RelayContracts/RelayContracts.csproj` with the following.

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <GenerateDocumentationFile>true</GenerateDocumentationFile>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="LogixTagRelay" Version="0.8.1-preview.1" />
    <PackageReference Include="LogixTagRelay.Build" Version="0.8.1-preview.1" PrivateAssets="all" />
    <LogixContract Include="Contracts\Telemetry.ltrx.json" />
  </ItemGroup>
</Project>
```

Replace `RelayApp/RelayApp.csproj` with the following. `LogixTagRelay.Logix` is the live explicit driver. `LogixTagRelay.Emulation` is used by the first test.

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net8.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="..\RelayContracts\RelayContracts.csproj" />
    <PackageReference Include="LogixTagRelay.Logix" Version="0.8.1-preview.1" />
    <PackageReference Include="LogixTagRelay.Emulation" Version="0.8.1-preview.1" />
  </ItemGroup>
</Project>
```

Build once to generate and compile the model.

```powershell
dotnet restore --configfile .\NuGet.Config
dotnet build --no-restore
```

The generated class is available to the app through its project reference. Open the `RelayConsumer` solution in Visual Studio, set `RelayApp` as the startup project, and type `tags.` in the example below to discover the generated members. The class library emits XML documentation and the build copies `contracts/Telemetry.ltrx/logixrelay.json` to the app's output. `TelemetryTags.CreateConfiguration()` gives the app the matching configuration without resolving a JSON path.

For more contracts, copy each `contract.ltrx.json` to a distinct filename, add a `LogixContract` item for each, and give the contracts unique fully qualified C# class names. Use the `.ltrx.json` files as your source and let MSBuild compile the generated C#.

## 4. Run a read and write without a PLC

Replace `RelayApp/Program.cs` with the following. The memory store plays the PLC side of the contract for this test. `DemoCount` is the PLC path and `Tag_DemoCount` is the generated C# property.

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

```powershell
dotnet run --project .\RelayApp
```

Expect `PLC to app: 41` and `App to PLC: 42`. To try PLC-side changes from a GUI or script, load the same `logixrelay.json` in the [contract memory emulator](EMULATION.md). It supports both explicit and implicit contracts.

## 5. Run the explicit contract against the PLC

Check the controller address and CIP route in the saved contract. Use a test controller or tags that your application is authorized to access. For a read-only check, select PLC → app directions and leave the heartbeat and trigger reply tags blank. A configured heartbeat or trigger reply can write independently of assignments to a data tag.

Replace `RelayApp/Program.cs` with this live example. The app logs changes and faults. Assign a writable `Value` from your own application logic when you want to submit a command.

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

Run `dotnet run --project .\RelayApp` and press **Ctrl+C** to stop. The live session follows the contract's timer or DINT trigger settings. For a triggered group, the PLC requests an update by changing the DINT to a new nonzero value. The optional handled mirror echoes the trigger. A configured signed DINT reply uses a positive ACK and negative NACK. Inspect `telemetry.Diagnostics.Snapshot` for missed intervals and recovery state. In a desktop UI, marshal tag change callbacks to the UI thread.

For a command-only tag, `Value` holds the requested command and `ObservedValue` holds the PLC readback. Repeat `AddExplicit` with a new generated model and a unique session name for each explicit contract. Give each write/control tag a single owner on the same controller and route.

## 6. Add the implicit contract to the same app

1. Add its `.ltrx.json` to `RelayContracts` as a separate `LogixContract` item, following step 3. This example assumes that its C# class is `IoTags` in `MyPlant.Contracts`.
2. In Studio 5000, import the supplied `GenericEthernetModule.L5X` if needed, or configure the existing module using `ModuleSetup.txt`. Use the PC NIC address, matching SINT lengths in both directions, and the generated assembly instances and RPI. Each direction includes the relay header and has a 480-byte maximum.
3. Import `RelayRoutine.L5X` into the scheduled program where you intend to call it. Verify that the selected application tags already exist, then add the JSR for `<module>_PS_Sync` to that program. Use the generated `<module>_PS_ApplyCommands` bit under your PLC's command permission logic. See [PLC setup and imports](PLC_SETUP.md) for the staging and watchdog behavior.
4. Add `<PackageReference Include="LogixTagRelay.Opener" Version="0.8.1-preview.1" />` to the app's `ItemGroup` and `using LogixTagRelay.Opener;` to `Program.cs`. Build and run the app as a Windows x64 process. `Get-NetAdapter` lists the NIC names. Use the NIC whose IPv4 address is configured on the PLC module and make sure the demo or another adapter instance has released the EtherNet/IP ports.
5. Replace the optional I/O comment in step 5 with the code below. Substitute your PC NIC name and a device serial number unique on that network. Keep `await host.RunAsync(stop.Token);` after the registration so cancellation finishes both sessions before disposal.

```csharp
var ioTags = new IoTags();
var ioConfig = IoTags.CreateConfiguration();
await using var io = new OpenerAdapter(ioConfig, ioTags,
    nicNameOrIndex: "Ethernet", deviceSerialNumber: 0x00001234, recovery: recovery);
host.Add("implicit-io", io);
```

The host supports multiple explicit groups and one implicit group. To run implicit alone, keep the host and cancellation code and register only `io`. If the implicit contract has a heartbeat, the adapter handles it through an explicit connection.

## 7. Refresh a contract or use the downloaded C# directly

**Refresh the build-generated model.** Edit and save the contract in the editor, download the interface again, and replace the matching `.ltrx.json` in the class library. Build the solution. If the implicit mapping changed, update the PLC routine and module from the same download and verify both peers together.

**Compile the downloaded model directly.** As an alternative to step 3, keep the core `LogixTagRelay` reference in `RelayContracts` and omit the `LogixTagRelay.Build` reference and `LogixContract` item for that model. Copy `GeneratedTags.cs` and its `logixrelay.json` into `RelayContracts/Contracts/Telemetry`. SDK-style projects compile the C# file automatically. The generated class still provides `CreateConfiguration()`. To load the JSON from disk instead, add this item to `RelayContracts.csproj` and read the matching file from the app's output directory.

```xml
<None Update="Contracts\Telemetry\logixrelay.json" CopyToOutputDirectory="PreserveNewest" />
```

```csharp
var config = SyncConfiguration.FromJson(File.ReadAllText(
    Path.Combine(AppContext.BaseDirectory, "Contracts", "Telemetry", "logixrelay.json")));
```

Keep one compiled source for each generated C# type. If you switch from direct compilation to MSBuild generation, remove the downloaded `.cs` from the project before rebuilding.

## Troubleshooting

| Symptom | What to check |
| --- | --- |
| Restore cannot find a relay package | Check that the pinned `.nupkg` is in the solution's `packages` directory and that restore uses the solution's `NuGet.Config`. |
| `LTR001` during build | Read the generator message for the specific contract. Check its catalog, selected paths and C# names. |
| Generated type is missing or duplicated | Check the namespace and class in the editor, the app's project reference and the `LogixContract` item. Compile each generated type once. |
| Explicit connection or tag read fails | Check the fault's operation, endpoint, route and tag path. Reachability, EtherNet/IP session registration and tag access are separate steps. |
| Implicit I/O connects but values remain stale | Check the scheduled JSR, module sizes and instances, the PLC watchdog and permission bit, and that the PLC and app use the same contract download. |

See [contract project details](CONTRACT_PROJECTS.md), [hosting and recovery](HOSTING_AND_RECOVERY.md), and [heartbeat and errors](LIVENESS_AND_ERRORS.md).
