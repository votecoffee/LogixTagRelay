using LogixTagRelay;

static class TriggerTests
{
    public static async Task Run(SyncConfiguration source, Action<bool, string> check)
    {
        var config = source with { Explicit = new(TriggerTag: "RequestSequence", HandledTriggerTag: "Handled", ResponseTag: "Response") };
        config.Validate();
        var model = new TestModel(config);
        var transport = new FakeTransport();
        foreach (var v in model.Variables) transport.Values[v.Map.Path] = v.BoxedValue;
        transport.Values["RequestSequence"] = 0;
        var session = new ExplicitSession(config, model, transport);
        check(await session.PollTriggerOnceAsync() is null && transport.Writes.Count == 0, "zero DINT trigger does not execute or reply");
        transport.Values["RequestSequence"] = 200000000;
        var result = await session.PollTriggerOnceAsync();
        check(result is { Request: 200000000, Succeeded: true, NotificationsDelivered: true } &&
            transport.Writes.SequenceEqual(new (string, object)[] { ("Response", 0), ("Response", 200000000), ("Handled", 200000000) }), "DINT busy, positive ACK, then exact handled mirror");
        check(await session.PollTriggerOnceAsync() is null, "held nonzero trigger is handled once");
        transport.Values["RequestSequence"] = 1;
        check((await session.PollTriggerOnceAsync())?.Request == 1, "positive DINT counter rollover triggers another group");
        transport.Values["RequestSequence"] = 0; await session.PollTriggerOnceAsync();
        transport.Values["RequestSequence"] = 1;
        check((await session.PollTriggerOnceAsync())?.Request == 1, "zero re-arms a repeated nonzero DINT value");
        transport.Values["RequestSequence"] = -12;
        await session.PollTriggerOnceAsync();
        check(Equals(transport.Values["Response"], 12) && Equals(transport.Values["Handled"], -12), "signed trigger retains exact mirror and positive ACK magnitude");
        var command = (PlcVariable<float>)model.Variables.Single(v => v.Map.Path == "Command.Setpoint");
        command.Value = 52;
        transport.FailPaths.Add(command.Map.Path);
        transport.Values["RequestSequence"] = 7;
        try { await session.PollTriggerOnceAsync(); throw new Exception("Expected failure"); }
        catch (IOException) { }
        check(Equals(transport.Values["Response"], -7) && Equals(transport.Values["Handled"], 7) && command.Dirty &&
            session.LastTriggerResult is { Succeeded: false, NotificationsDelivered: true }, "failed data group publishes negative DINT NACK and handled mirror before faulting");
        check(await session.PollTriggerOnceAsync() is null, "failed held request is not automatically replayed");
        transport.FailPaths.Clear(); transport.FailPaths.Add("Handled");
        transport.Values["RequestSequence"] = 8;
        try { await session.PollTriggerOnceAsync(); throw new Exception("Expected mirror failure"); } catch (IOException) { }
        check(Equals(transport.Values["Response"], 8) && session.LastTriggerResult is { Succeeded: true, NotificationsDelivered: false }, "mirror failure cannot replace a delivered ACK with a contradictory NACK");
        transport.FailPaths.Clear(); transport.Values["RequestSequence"] = int.MinValue;
        try { await session.PollTriggerOnceAsync(); throw new Exception("Expected DINT boundary rejection"); } catch (ArgumentOutOfRangeException) { }
        check(Equals(transport.Values["Response"], int.MinValue) && Equals(transport.Values["Handled"], int.MinValue), "minimum DINT is NACKed before data work when signed replies are enabled");
        foreach (var invalid in new[] { new ExplicitOptions(ResponseTag: "R"), new ExplicitOptions(TriggerTag: "R", ResponseTag: "R"),
            new ExplicitOptions(TriggerTag: "R", HandledTriggerTag: "Command.Setpoint"), new ExplicitOptions(TriggerTag: "bad();") })
        {
            try { (config with { Explicit = invalid }).Validate(); throw new Exception("Expected invalid control settings"); }
            catch (FormatException) { }
        }
        check(true, "invalid or overlapping trigger control addresses rejected");
    }
}
