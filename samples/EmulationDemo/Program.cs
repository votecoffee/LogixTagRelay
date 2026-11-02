using System.Globalization;
using LogixTagRelay;
using LogixTagRelay.Emulation;
using LogixTagRelay.Generated;

string mode = args.Length > 0 ? args[0] : "explicit";
if (mode is not ("explicit" or "implicit")) throw new ArgumentException("Usage: explicit|implicit [position] [--triggered]");
float target = args.Length > 1 ? float.Parse(args[1], CultureInfo.InvariantCulture) : 25f;
if (!float.IsFinite(target)) throw new ArgumentException("Position must be finite.");
string contractName = args.Contains("--triggered") ? "logixrelay-triggered.json" : "logixrelay.json";
var config = SyncConfiguration.FromJson(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, contractName)));
var tags = new GeneratedTags();
tags.Tag_Motion_002ECommandPosition.Value = target;
using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(30));
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancel.Cancel(); };
await using var emulator = await EmulatorClient.ConnectAsync(config, cancellationToken: cancel.Token);
Console.WriteLine($"{mode}: requested {target}. Run motion.js in the emulator GUI; Ctrl+C stops this client.");
Task syncing = mode == "explicit" ? new ExplicitSession(config, tags, emulator).RunAsync(cancel.Token) : emulator.RunImplicitAsync(tags, cancel.Token);
bool reached = false;
try
{
    while (!syncing.IsCompleted)
    {
        float actual = tags.Tag_Motion_002EActualPosition.Value;
        bool inPosition = tags.Tag_Motion_002EInPosition.Value;
        Console.WriteLine($"Command={target:R}, Actual={actual:R}, InPosition={inPosition}, Dirty={tags.Tag_Motion_002ECommandPosition.Dirty}");
        if (!tags.Tag_Motion_002ECommandPosition.Dirty && inPosition && Math.Abs(actual - target) <= 0.01f) { reached = true; break; }
        await Task.Delay(250, cancel.Token);
    }
}
catch (OperationCanceledException) when (cancel.IsCancellationRequested) { }
finally
{
    cancel.Cancel();
    try { await syncing; } catch (OperationCanceledException) when (cancel.IsCancellationRequested) { }
}
if (!reached) throw new TimeoutException("Position was not reached. Check the emulator contract, script and trigger settings.");
Console.WriteLine("Motion completed using the generated contract without a PLC.");
