# Writing code for simulated I/O

Use the PLC memory emulator to test what your app does when it sends a command and receives feedback. This walkthrough makes a simulated axis follow a position command. The same PLC-side script works with an explicit session and an implicit I/O session.

| Where the code runs | What it controls |
| --- | --- |
| C# in the consuming app | Sends commands through generated tag properties and reads feedback. |
| JavaScript in the emulator | Acts as the PLC. Reads commands and changes simulated sensor and feedback values. |
| C# with `PlcMemory` in a test | Seeds or changes PLC-side values directly in the test process. |

## 1. Open the PLC memory emulator

From the LogixTagRelay repository root, start the emulator and open it in a browser. The **PLC memory emulator** is the page with **PLC tag memory** and **JavaScript response script**. The Contract Editor and the demo's **Simulation** mode have separate screens. [Open the PLC memory emulator](http://127.0.0.1:5275/)

```powershell
dotnet run --project tools/LogixTagRelay.Emulator -c Release
```

Choose **Load motion example** to start with fresh motion memory. It loads four tags and sets `Motion.Speed` to `10` position units per second.

| PLC path | Type | How the example uses it |
| --- | --- | --- |
| `Motion.CommandPosition` | REAL | The C# app requests a position. |
| `Motion.ActualPosition` | REAL | The script moves the simulated axis and publishes its position. |
| `Motion.InPosition` | BOOL | The script sets this when the actual position is within tolerance. |
| `Motion.Speed` | REAL | Set this from the emulator GUI to change how fast the simulated axis moves. |

For your own project, select the `logixrelay.json` from one contract folder in **Download interface**, then choose **Load contract**. Use the matching generated model in your app. The GUI holds one loaded contract at a time. Loading a contract creates fresh memory and stops the previous script, so reconnect any running client.

## 2. Write the PLC response script

Choose **Stop** if a script is running. Paste this code into **Script source**. Each execution is one tick. `dt` is the configured interval in seconds, so a 100 ms tick at speed 10 moves up to one position unit.

```javascript
const target = plc.get('Motion.CommandPosition');
const actual = plc.get('Motion.ActualPosition');
const speed = Math.abs(plc.get('Motion.Speed'));
const error = target - actual;
const travel = Math.min(Math.abs(error), speed * dt);
const next = actual + Math.sign(error) * travel;

plc.set('Motion.ActualPosition', next);
plc.set('Motion.InPosition', Math.abs(target - next) <= 0.01);

if (plc.changed('Motion.CommandPosition')) {
  plc.log('New target: ' + target);
}
```

Try it before writing any C#.

1. In **PLC tag memory**, enter `3` in the `Motion.CommandPosition` row and choose **Apply**. Leave `Motion.ActualPosition` at `0` and `Motion.Speed` at `10`.
2. Set **Tick interval (ms)** to `100`. Choose **Step once** three times. `Motion.ActualPosition` should read `1`, `2`, then `3`. `Motion.InPosition` becomes `true` on the third step.
3. Choose **Run script** for automatic ticks. Choose **Download script** to save the current code as a `.js` file. Use **Open script** to load it again, then **Run script** or **Step once** to execute it.

Use the PLC paths from the memory table in JavaScript. For example, `Stations[1,2].Ready` addresses one array element member. The generated C# property names can differ from those paths.

| Script API | Use |
| --- | --- |
| `plc.has(path)` | Check whether an optional tag exists in the loaded contract. |
| `plc.get(path)` | Read a typed value, including a value staged earlier in the same tick. |
| `plc.set(path, value)` | Stage a typed PLC-side write. All writes from a successful tick commit together. |
| `plc.changed(path)` | Compare the tick's starting value with the last committed tick. It returns `true` on the first tick. |
| `plc.log(message)` | Write a message to the script log. |
| `state` | Keep counters, timers or flags across ticks in a JSON object. Script source changes reset it. |

Use booleans for BOOL, strings for STRING and numbers for REAL and DINT. A `LINT`, `ULINT`, `DT`, `TIME`, `LTIME` or `LDT` read returns a JavaScript `BigInt`. Use values such as `1500000n` for a `TIME` of 1.5 seconds, and convert BigInt to text before saving it in `state`. Custom STRING values must fit the capacity defined in the contract. [Full script and type reference](EMULATION.md)

## 3. Send a command from C#

The app uses the `LogixTagRelay.Emulation` NuGet package and a generated tag class. For this exact example, create a console project in a solution directory with the preview NuGet feed configured, then copy the repository's generated motion model. If you already have a project and its matching generated class, add the package to that project and use your class and property names. [NuGet and project setup](CONSUMER_MANUAL.md)

```powershell
$relayRepo = Read-Host 'Path to the LogixTagRelay repository'
dotnet new console --name MotionClient --framework net8.0 --no-restore
dotnet add .\MotionClient\MotionClient.csproj package LogixTagRelay.Emulation --version 0.8.1-preview.1 --no-restore
Copy-Item -LiteralPath (Join-Path $relayRepo 'samples/EmulationDemo/GeneratedTags.g.cs') `
    -Destination .\MotionClient
dotnet restore .\MotionClient\MotionClient.csproj --configfile .\NuGet.Config
```

Replace `MotionClient/Program.cs` with the following. The generated class supplies its matching configuration. The client uses the loopback emulator address by default.

```csharp
using LogixTagRelay;
using LogixTagRelay.Emulation;
using LogixTagRelay.Generated;

bool implicitMode = args is ["implicit"];
var tags = new GeneratedTags();
var config = GeneratedTags.CreateConfiguration();
const float target = 25f;

using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(15));
await using var emulator = await EmulatorClient.ConnectAsync(config, cancellationToken: stop.Token);
tags.Tag_Motion_002ECommandPosition.Value = target;

Task syncing = implicitMode
    ? emulator.RunImplicitAsync(tags, stop.Token)
    : new ExplicitSession(config, tags, emulator).RunAsync(stop.Token);

try
{
    while (true)
    {
        float actual = tags.Tag_Motion_002EActualPosition.Value;
        if (!tags.Tag_Motion_002ECommandPosition.Dirty &&
            tags.Tag_Motion_002EInPosition.Value && Math.Abs(actual - target) <= 0.01f)
        {
            Console.WriteLine($"Reached {actual} using {(implicitMode ? "implicit" : "explicit")} I/O.");
            break;
        }

        if (syncing.IsCompleted)
        {
            await syncing;
            throw new InvalidOperationException("Session stopped before the axis reached its target.");
        }
        await Task.Delay(50, stop.Token);
    }
}
catch (OperationCanceledException) when (stop.IsCancellationRequested)
{
    throw new TimeoutException("Check that the response script is running and Motion.Speed is above zero.");
}
finally
{
    stop.Cancel();
    try { await syncing; }
    catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
}
```

Keep the response script running and `Motion.Speed` above zero. Run one mode at a time.

```powershell
dotnet run --project .\MotionClient -- explicit
dotnet run --project .\MotionClient -- implicit
```

Each run should report that it reached `25`. In the GUI, **Last API value** for `Motion.CommandPosition` shows `25`, while **PLC value** shows the current memory. To watch motion again after the first run, stop the script, reset `Motion.ActualPosition` to `0` in the GUI, then restart the script and client. For a different target, change `target` in the C# example.

Keep **Accept implicit commands** checked for the implicit run and use a contract with implicit settings. Both modes use the same PLC-side values and script over loopback HTTP. The implicit path exercises the contract's image and merge behavior. Live adapter, network and controller timing checks are separate.

## 4. Exercise a trigger or a failure

### Triggered explicit I/O

In the emulator, load `samples/EmulationDemo/logixrelay-triggered.json` and set `Motion.Speed` to `10`. Append this code to the response script in step 2 and choose **Run script**. The script holds `RequestSequence` until the app mirrors it to `HandledSequence`. It issues a new nonzero DINT request when the current one has been handled.

```javascript
const request = plc.get('RequestSequence');
if (request === plc.get('HandledSequence')) {
  plc.set('RequestSequence', request >= 200000000 ? 1 : request + 1);
}
```

Run the repository client that reads the matching triggered configuration. The GUI shows the request, the handled value and the positive ACK in `ResponseSequence` after a successful update. A failed update can return a negative NACK. Leave the response and handled tags to the client during this test.

```powershell
dotnet run --project samples/EmulationDemo -c Release -- explicit 25 --triggered
```

### Invalid PLC write

With the motion contract loaded, stop the script, record `Motion.ActualPosition`, and step this code once. `Motion.InPosition` requires a boolean. The error appears in the script panel and the earlier staged position change is discarded. Restore the valid script afterward.

```javascript
plc.set('Motion.ActualPosition', 99);
plc.set('Motion.InPosition', 1);
```

### Manual feedback

With the script stopped, use **Apply** in the memory table to change `Motion.ActualPosition` and `Motion.InPosition`. An active app session can observe the values. Resume the script when you want it to own those fields again.

## 5. Set simulated PLC values directly in a C# test

For an in-process test, use `PlcMemory` and `MemoryExplicitTransport` from the same NuGet package. This `Program.cs` drives feedback from C# instead of the browser and uses the motion class from step 3. `Apply` validates both feedback values and commits them as one batch.

```csharp
using LogixTagRelay;
using LogixTagRelay.Emulation;
using LogixTagRelay.Generated;

var config = GeneratedTags.CreateConfiguration();
var tags = new GeneratedTags();
var memory = new PlcMemory(config);
await using var transport = new MemoryExplicitTransport(memory);
var session = new ExplicitSession(config, tags, transport);

tags.Tag_Motion_002ECommandPosition.Value = 12.5f;
await session.SynchronizeOnceAsync();

float request = (float)memory.Read("Motion.CommandPosition");
if (request != 12.5f) throw new InvalidOperationException("Command did not reach PLC memory.");

memory.Apply([
    new MemoryWrite("Motion.ActualPosition", request),
    new MemoryWrite("Motion.InPosition", true)
], writer: "Test PLC");
await session.SynchronizeOnceAsync();

if (!tags.Tag_Motion_002EInPosition.Value || tags.Tag_Motion_002EActualPosition.Value != request)
    throw new InvalidOperationException("PLC feedback did not reach the app.");

Console.WriteLine($"In-process round trip passed at {request}.");
```

See [emulator reference](EMULATION.md) for headless implicit transport, script limits and memory snapshots.
