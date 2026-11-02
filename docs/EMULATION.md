# Contract memory emulator

The emulator supplies PLC-side tag memory, not PLC program execution. It runs as a
separate loopback process so a consuming .NET app, manual GUI edits and scripts share
the same values. It requires no PLC, Studio 5000 or native OpENer DLL.

## Start and try the motion example

From the repository root:

```powershell
dotnet run --project tools/LogixTagRelay.Emulator -c Release
```

Open <http://127.0.0.1:5275>. The default motion contract and `motion.js` are loaded;
scripts do not start automatically. Click **Run script**, then use another terminal:

```powershell
dotnet run --project samples/EmulationDemo -c Release -- explicit 25
dotnet run --project samples/EmulationDemo -c Release -- implicit 50
```

The sample client sets command position, waits for actual position and in-position,
then cancels and awaits its session. It exits after 30 seconds if motion never completes.
The GUI continues to show the retained memory and last API requests. Edit `Motion.Speed`
to change the simulated motion speed. Stop the script to test entirely manual PLC responses.

Load a generated `logixrelay.json` using **Load contract**, or start with:

```powershell
dotnet run --project tools/LogixTagRelay.Emulator -c Release -- --contract (Resolve-Path samples/StringsAndArraysDemo/logixrelay.json).Path
```

Loading a contract stops its old script and creates fresh memory and a new instance ID.
Connected clients must reconnect. A stale client cannot write a replacement contract,
even when its field hash happens to match. The full fingerprint covers field mapping,
image lengths, explicit control paths and whether implicit settings exist; PLC gateway
addresses and polling cadence do not affect emulated memory identity.

## Use from a consuming app

Reference the optional `LogixTagRelay.Emulation` package and the same generated C#/JSON
used for a real PLC. Load that JSON in the emulator GUI before connecting.

```csharp
using LogixTagRelay;
using LogixTagRelay.Emulation;
using LogixTagRelay.Generated;

var config = SyncConfiguration.FromJson(File.ReadAllText("logixrelay.json"));
var tags = new GeneratedTags();
await using var emulator = await EmulatorClient.ConnectAsync(config);

// Explicit: the normal timer or DINT trigger scheduler uses the emulator transport.
await new ExplicitSession(config, tags, emulator).RunAsync(cancellationToken);

// Alternatively, for an implicit contract:
// await emulator.RunImplicitAsync(tags, cancellationToken);
```

Use one implicit client per loaded contract, and select one session mode per model.
Cancel and await the loop before disposing its client. Both modes use loopback HTTP
to reach the same GUI memory; these requests are not EtherNet/IP packets. Implicit
mode exercises protocol v3, complete image sizes, bootstrap, frozen baseline matching,
duplicate rejection, byte-level PLC-first merge and API readback reconciliation.
It does not exercise OpENer, CIP connections, RPI negotiation or real controller timing.

For headless unit tests, use `PlcMemory`, `MemoryExplicitTransport`, `ImplicitPlc` and
`ScriptRunner` directly in the test process. GUI edits and scripts may change any
PLC-side field. API writes are limited to command/bidirectional fields and configured reply tags.
The GUI shows current values and last requested API values separately, which also
makes PLC-winning implicit conflicts visible.

## JavaScript ticks

Each execution receives `plc`, `dt` in seconds and a persistent JSON `state` object.
The default tick interval is 100 ms. **Step once** executes a single tick while stopped.
Changing source resets state and change history; stopping and restarting unchanged
source retains them. Opening/editing a script does not run the new source.

```javascript
const target = plc.get('Motion.CommandPosition');
const actual = plc.get('Motion.ActualPosition');
const speed = Math.abs(plc.get('Motion.Speed'));
const next = actual + Math.sign(target - actual)
  * Math.min(Math.abs(target - actual), speed * dt);
plc.set('Motion.ActualPosition', next);
plc.set('Motion.InPosition', Math.abs(target - next) <= 0.01);
if (plc.changed('Motion.CommandPosition')) plc.log('Target: ' + target);
state.ticks = (state.ticks || 0) + 1;
```

| Helper | Behavior |
| --- | --- |
| `plc.has(path)` | Whether the loaded memory contains the exact tag/member/array-element path; case insensitive |
| `plc.get(path)` | Typed value; reads staged writes from the same tick first |
| `plc.set(path, value)` | Stage a PLC memory change; validate type/range/capacity before commit |
| `plc.changed(path)` | Current value differs from the last committed tick; true on the first tick |
| `plc.log(message)` | Append to the bounded GUI log |

Numeric primitives use JavaScript numbers, BOOL uses boolean and STRING uses string.
LINT/ULINT/DT/TIME/LTIME/LDT reads return **BigInt**; use `plc.set('Counter', plc.get('Counter') + 1n)` to preserve
all 64 bits. These 64-bit writes accept BigInt, exact integer text or safe integer numbers.
Store BigInts as text in `state`, which must remain JSON serializable. GUI and HTTP
values use invariant text to avoid JSON/JavaScript loss of 64-bit precision. Custom string
capacities and the library's lossless Latin-1 rules still apply. Use Logix array paths
such as `Stations[1,2].Name` when reading or writing expanded array elements.

A tick stages all writes before committing. Syntax errors, exceptions, invalid types,
invalid string lengths or execution-limit errors stop the script, expose diagnostics,
and leave tag memory and saved script state unchanged. If memory changes concurrently
during script evaluation, the tick is discarded and retried at the next interval.
Unchanged cyclic writes update API diagnostics without changing the memory revision,
so repeated frames cannot starve scripts that are responding to stable commands.
Successful ticks commit atomically to emulator memory; this does not imply PLC atomicity.

Use trusted local scripts. The engine exposes primitive JSON, not CLR/filesystem/network
objects. Each tick has a 20000-statement limit, 16 MB allocation limit, recursion depth
64 and one-second interpreter timeout (including cold execution). These are guards
against common mistakes, not an operating-system security boundary. Tick time is a
nominal step, not a real-time scheduling guarantee; overruns coalesce.

## DINT trigger tests

Load `samples/EmulationDemo/logixrelay-triggered.json`. Open `motion.js` from that
directory, set `Motion.Speed` to a positive value and click **Run script**. Then:

```powershell
dotnet run --project samples/EmulationDemo -c Release -- explicit 25 --triggered
```

That script also advances `RequestSequence` only after it equals `HandledSequence`,
rolling over to 1 at 200000000. The GUI displays the trigger, exact handled mirror and
signed DINT response. Alternatively, stop scripting and edit the trigger manually to
test zero, held values, new nonzero values and rollover. Hold each request until handled.

## Manual scenarios and retained memory

Use **Accept implicit commands** to hold a reply while editing PLC memory, then
resume before the client's stale timeout to test PLC-first conflicts. A disabled or
invalid exchange leaves its frozen output snapshot unchanged; a completed new exchange
publishes current memory and saves the next baseline. Invalid byte-merged STRING LENs
prevent all implicit writes and acknowledgement, matching the generated wrapper.

**Save memory** downloads exact text values tied to the contract fingerprint.
**Restore memory** validates all values before applying them as one batch and stops
the script. Files stay on the user's machine. The service binds to loopback, uses no
CORS allowances and requires a custom header on write routes. It is a local test tool,
not an authenticated shared server. No state is retained automatically after process exit.

## Local verification

```powershell
dotnet run --project tests/LogixTagRelay.Tests -c Release
dotnet run --project tools/LogixTagRelay.Emulator -c Release -- --self-test true
```

The second command starts an owned HTTP host on an ephemeral loopback port, exercises
the real routes/client and exits. It checks explicit, implicit and DINT-triggered motion,
custom STRING/LINT/array paths, rejected stale clients, manual edits, script failures,
memory restore and static GUI serving. It requires no PLC or EtherNet/IP adapter.

DT and TIME editors accept signed integer microseconds (DT since Unix epoch UTC;
TIME is a duration). Scripts return these values as `BigInt`, just like LINT/ULINT.
Use `plc.set("Telemetry.Elapsed", 1500000n)` for 1.5 seconds. USINT/UINT/UDINT use
ordinary JavaScript safe integers, with strict unsigned range checks at commit.
See [type mappings and notifications](TYPES_AND_EVENTS.md).

Managed `ImplicitContractSession` can use `EmulatorClient` or `MemoryImplicitTransport`
through `IImplicitTransport`, including nonce baseline recovery. `samples/HostingDemo`
runs multiple explicit groups alongside one implicit image in memory.
