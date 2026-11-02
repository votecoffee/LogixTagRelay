using LogixTagRelay;
using LogixTagRelay.Design;

static class ContractProjectTests
{
    public static void Run(Action<bool, string> check)
    {
        using var file = File.OpenRead(Path.Combine(AppContext.BaseDirectory, "Types.L5X"));
        var catalog = L5xCatalog.Read(file);
        var project = new ContractProject("Telemetry", [new("Telemetry", Direction.Bidirectional)], catalog,
            Namespace: "MyApp.Contracts.Telemetry", ClassName: "TelemetryTags", Implicit: new(111, 110, 112));
        var bundle = ContractProject.FromJson(project.ToJson()).Generate();
        check(bundle.ModelCs.Contains("namespace MyApp.Contracts.Telemetry;") && bundle.ModelCs.Contains("public TelemetryTags()") &&
            bundle.Configuration.Implicit!.PlcInputAssembly == 111 && bundle.ModuleInstructions.Contains("Input assembly: 111"), "portable project retains C# identity, selections and module assemblies");
        check(project.Generate().ModelCs == bundle.ModelCs && ContractComparison.Compare(bundle.Configuration, bundle.Configuration).Count == 0,
            "project regeneration is deterministic and equal schemas have no differences");
        var relocated = (project with { Gateway = "192.0.2.10", Route = "1,1", PcAddress = "192.0.2.20" }).Generate();
        check(relocated.ModelCs.Contains("192.0.2.10") && relocated.ModuleInstructions.Contains("192.0.2.20"), "project embeds matching endpoint settings and retains the module PC address");
        var changed = project with { Selections = [new("Telemetry.Code", Direction.Bidirectional)] };
        check(ContractComparison.Compare(bundle.Configuration, changed.Generate().Configuration).Any(c => c.Breaking && c.Path == "Telemetry.Count16"), "schema comparison identifies breaking removals");
        bool Reject(Action action) { try { action(); return false; } catch (FormatException) { return true; } }
        check(Reject(() => (project with { Namespace = "namespace.bad" }).Generate()) && Reject(() => ContractProject.FromJson(project.ToJson().Replace("\"version\": 1", "\"version\": 1, \"version\": 1"))), "project validation rejects invalid C# names and duplicate JSON keys");
        var pinned = project with { Catalog = null, SourceL5x = "Types.L5X", SourceSha256 = new string('0', 64) };
        check(Reject(() => pinned.Generate(AppContext.BaseDirectory)), "source-pinned projects reject changed L5X input");
    }
}
