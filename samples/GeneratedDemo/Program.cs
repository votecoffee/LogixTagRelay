using LogixTagRelay;
using LogixTagRelay.Generated;
using LogixTagRelay.Logix;
using LogixTagRelay.Opener;

var config = SyncConfiguration.FromJson(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "logixrelay.json")));
var tags = new GeneratedTags();
tags.Tag_Command.Tag_Enable.Value = true;
tags.Tag_Command.Tag_Setpoint.Value = 125.5f;
ModelValidation.Validate(config, tags);
// Without arguments this consumer is an offline, deterministic codec demo.
if (args.Length == 0)
{
    byte[] image = new ImplicitImage(config, tags).BuildAppToPlc();
    Console.WriteLine($"Generated nested UDT model consumed; {image.Length}-byte PC-to-PLC image.");
    Console.WriteLine($"Enable={tags.Tag_Command.Tag_Enable.Value}, Setpoint={tags.Tag_Command.Tag_Setpoint.Value}");
    return;
}
using var cancel = new CancellationTokenSource();
Console.CancelKeyPress += (_, eventArgs) => { eventArgs.Cancel = true; cancel.Cancel(); };
try
{
    if (args[0] == "explicit")
    {
        if (args.Length < 2) throw new ArgumentException("Usage: explicit <PLC IP> [route]");
        config = config with { Gateway = args[1], Route = args.Length > 2 ? args[2] : "1,0" };
        await using var driver = new LogixTransport(config.Gateway, config.Route);
        await new ExplicitSession(config, tags, driver).RunAsync(cancel.Token);
    }
    else if (args[0] == "implicit")
    {
        if (args.Length != 3) throw new ArgumentException("Usage: implicit <Windows NIC name or index> <unique device serial number>");
        await using var adapter = new OpenerAdapter(config, tags);
        await adapter.RunAsync(args[1], uint.Parse(args[2]), cancel.Token);
    }
    else throw new ArgumentException("Choose explicit or implicit. No arguments runs offline.");
}
catch (OperationCanceledException) when (cancel.IsCancellationRequested) { }
