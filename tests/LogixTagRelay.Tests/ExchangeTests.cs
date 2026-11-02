using LogixTagRelay;
using LogixTagRelay.Design;

static class ExchangeTests
{
    public static void Run(GeneratedBundle bundle, GeneratedBundle extended, Action<bool, string> check)
    {
        var plc = new RllSubset(bundle);
        var config = bundle.Configuration;
        var model = new TestModel(config);
        var codec = new ImplicitImage(config, model);
        var command = (PlcVariable<float>)model.Variables.Single(v => v.Map.Path == "Command.Setpoint");
        plc.Set("Command.Setpoint", 0x3f800000); plc.Set("LogixRelay_PS_ApplyCommands", 1);
        plc.Bytes("LogixRelay:I.Data", codec.BuildBootstrap()); plc.Scan();
        var initial = plc.Bytes("LogixRelay:O.Data");
        check(ImplicitImage.ReadInt(initial, 12) == 1 && initial[24..].SequenceEqual(plc.Bytes("LogixRelay_PS_LastSent")), "emitted wrapper saves exact module output payload at publication");
        codec.ReceivePlcToApp(initial, DateTimeOffset.UtcNow);
        command.Value = BitConverter.Int32BitsToSingle(0x3f810002);
        byte[] request = codec.BuildAppToPlc();
        plc.Set("Command.Setpoint", 0x3f800001); // PLC and API changed byte 0; only API changed byte 2.
        PlcTypes.Encode(config.Fields.Single(f => f.Path == "Status.Speed"), 999f, request.AsSpan(config.Fields.Single(f => f.Path == "Status.Speed").Offset));
        plc.Bytes("LogixRelay:I.Data", request); plc.Scan();
        var response = plc.Bytes("LogixRelay:O.Data");
        check(plc.Get("Command.Setpoint") == 0x3f810001 && plc.Get("Status.Speed") == 0 && ImplicitImage.ReadInt(response, 16) == ImplicitImage.ReadInt(request, 12), "emitted wrapper executes ST byte merge, preserves PLC conflicts and protects PLC-owned fields");
        codec.ReceivePlcToApp(response, DateTimeOffset.UtcNow);
        check(BitConverter.SingleToInt32Bits(command.Value) == 0x3f810001 && !command.Dirty, "closed exchange reconciles PLC-winning bytes into the typed API value");
        plc.Set("Command.Setpoint", 0x3f820003); plc.Scan();
        check(plc.Get("Command.Setpoint") == 0x3f820003 && plc.Bytes("LogixRelay:O.Data").SequenceEqual(response), "duplicate API packets neither reapply commands nor replace the frozen baseline");
        byte[] next = codec.BuildAppToPlc();
        check(BitConverter.SingleToInt32Bits((float)PlcTypes.Decode(command.Map, next.AsSpan(command.Map.Offset))) == 0x3f810001, "clean API values echo the accepted PLC image");
        ImplicitImage.WriteInt(next, 16, 777);
        plc.Bytes("LogixRelay:I.Data", next); plc.Scan();
        check(plc.Bytes("LogixRelay:O.Data").SequenceEqual(response) && plc.Get("Command.Setpoint") == 0x3f820003, "mismatched snapshot reference cannot merge or acknowledge a command");
        ImplicitImage.WriteInt(next, 16, ImplicitImage.ReadInt(response, 12));
        plc.Bytes("LogixRelay:I.Data", next); plc.Scan();
        codec.ReceivePlcToApp(plc.Bytes("LogixRelay:O.Data"), DateTimeOffset.UtcNow);
        check(BitConverter.SingleToInt32Bits(command.Value) == 0x3f820003, "PLC changes made while waiting win the next round trip");
        var extPlc = new RllSubset(extended);
        extPlc.Set("LogixRelay_PS_ApplyCommands", 1); extPlc.Scan();
        var extModel = new TestModel(extended.Configuration); var extCodec = new ImplicitImage(extended.Configuration, extModel);
        extCodec.ReceivePlcToApp(extPlc.Bytes("LogixRelay:O.Data"), DateTimeOffset.UtcNow);
        var recipe = (PlcVariable<string>)extModel.Variables.Single(v => v.Map.Path == "Request.Recipe"); recipe.Value = "Recipe";
        var extRequest = extCodec.BuildAppToPlc(); extPlc.Bytes("LogixRelay:I.Data", extRequest); extPlc.Scan();
        extCodec.ReceivePlcToApp(extPlc.Bytes("LogixRelay:O.Data"), DateTimeOffset.UtcNow);
        check(recipe.Value == "Recipe" && !recipe.Dirty, "emitted extended wrapper round trips STRING, rectangular arrays and UDT fields");
        var text = new TagNode("Text", "Text", "Text256", "Read/Write", [], StringCapacity: 256);
        var number = new TagNode("Number", "Number", "DINT", "Read/Write", []);
        var stringBundle = Generator.Generate(new TagCatalog("Test", "35.03", [text, number]), [new("Text", Direction.AppToPlc), new("Number", Direction.AppToPlc)]);
        var stringPlc = new RllSubset(stringBundle); stringPlc.Set("LogixRelay_PS_ApplyCommands", 1); stringPlc.Scan();
        var stringModel = new TestModel(stringBundle.Configuration); var stringCodec = new ImplicitImage(stringBundle.Configuration, stringModel);
        stringCodec.ReceivePlcToApp(stringPlc.Bytes("LogixRelay:O.Data"), DateTimeOffset.UtcNow);
        ((PlcVariable<string>)stringModel.Variables[0]).Value = "X"; ((PlcVariable<int>)stringModel.Variables[1]).Value = 99;
        var invalidMerge = stringCodec.BuildAppToPlc(); stringPlc.Set("Text.LEN", 256);
        stringPlc.Bytes("LogixRelay:I.Data", invalidMerge); stringPlc.Scan();
        check(stringPlc.Get("Text.LEN") == 256 && stringPlc.Get("Number") == 0 && stringPlc.Get("LogixRelay_PS_MergedValid") == 0 &&
            ImplicitImage.ReadInt(stringPlc.Bytes("LogixRelay:O.Data"), 16) == 0, "invalid byte-merged STRING LEN rejects all destinations and acknowledgement");
    }
}
