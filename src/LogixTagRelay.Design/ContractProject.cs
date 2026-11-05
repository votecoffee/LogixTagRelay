using System.Text.Json;
using System.Text.RegularExpressions;

namespace LogixTagRelay.Design;

/// <summary>Portable, versioned generation inputs. Catalogs can be embedded or loaded from a relative L5X.</summary>
/// <param name="Name">Tag, controller or module display name from the source catalog.</param>
/// <param name="Selections">Catalog paths and requested synchronization directions.</param>
/// <param name="Catalog">Embedded source catalog; supply exactly one of Catalog and SourceL5x.</param>
/// <param name="SourceL5x">Relative or absolute L5X catalog source; resolved against the project directory.</param>
/// <param name="SourceSha256">Optional SHA-256 pin for the original L5X file; generation rejects a changed source.</param>
/// <param name="Namespace">Valid C# namespace for the generated contract.</param>
/// <param name="ClassName">Valid C# identifier for the generated contract type.</param>
/// <param name="IncludeImplicit">Whether to generate one symmetric implicit image and its PLC artifacts.</param>
/// <param name="ModuleName">Generic Ethernet Module identifier and staging prefix.</param>
/// <param name="SoftwareRevision">Studio 5000 revision associated with the source export.</param>
/// <param name="AssemblyBytes">Requested symmetric image length, or zero for the minimum aligned size.</param>
/// <param name="Explicit">Optional explicit interval, trigger, receipt and grouped-read settings.</param>
/// <param name="Implicit">Optional implicit assembly and timing settings.</param>
/// <param name="Gateway">PLC endpoint hostname or IP address.</param>
/// <param name="Route">CIP route to the intended controller.</param>
/// <param name="PcAddress">IPv4 address of the PC NIC serving the implicit adapter.</param>
/// <param name="Version">Portable project format version; currently 1.</param>
/// <param name="Heartbeat">Optional explicit-only BOOL heartbeat, independent of implicit payload layout.</param>
public sealed record ContractProject(string Name, List<TagSelection> Selections, TagCatalog? Catalog = null,
    string? SourceL5x = null, string? SourceSha256 = null, string Namespace = "LogixTagRelay.Generated",
    string ClassName = "GeneratedTags", bool IncludeImplicit = true, string ModuleName = "LogixRelay",
    string SoftwareRevision = "", int AssemblyBytes = 0, ExplicitOptions? Explicit = null,
    ImplicitOptions? Implicit = null, string Gateway = "192.168.1.10", string Route = "1,0", string PcAddress = "192.168.1.200", int Version = 1, HeartbeatOptions? Heartbeat = null)
{
    /// <summary>Serializes the configuration or project using the relay JSON format.</summary>
    public string ToJson() => JsonSerializer.Serialize(this, SyncConfiguration.Json) + "\n";
    /// <summary>Parses JSON, rejecting duplicate keys and unknown properties. Contract configurations also undergo layout validation.</summary>
    /// <param name="json">Serialized contract or project JSON.</param>
    public static ContractProject FromJson(string json)
    {
        using var document = JsonDocument.Parse(json);
        void Unique(JsonElement e)
        {
            if (e.ValueKind == JsonValueKind.Object)
            {
                var keys = new HashSet<string>(StringComparer.Ordinal);
                foreach (var p in e.EnumerateObject()) { if (!keys.Add(p.Name)) throw new FormatException("Duplicate project JSON key: " + p.Name); Unique(p.Value); }
            }
            else if (e.ValueKind == JsonValueKind.Array) foreach (var item in e.EnumerateArray()) Unique(item);
        }
        Unique(document.RootElement);
        return JsonSerializer.Deserialize<ContractProject>(json, SyncConfiguration.Json) ?? throw new FormatException("Empty contract project.");
    }
    /// <summary>Resolves and optionally hash-checks the catalog source, validates project identity and generates matching artifacts.</summary>
    /// <param name="projectDirectory">Base directory for resolving a relative source L5X path.</param>
    public GeneratedBundle Generate(string? projectDirectory = null) => Generate(projectDirectory, scopedMemberTypes: false);

    internal GeneratedBundle Generate(string? projectDirectory, bool scopedMemberTypes)
    {
        if (Version != 1 || Selections is null || Selections.Count == 0 || string.IsNullOrWhiteSpace(Name)) throw new FormatException("A version 1 named contract project with selections is required.");
        bool Identifier(string value) => Regex.IsMatch(value, "^[A-Za-z_][A-Za-z0-9_]*$") && !Keywords.Contains(value);
        if (!Identifier(ClassName) || !Namespace.Split('.').All(Identifier)) throw new FormatException("Namespace and class must be valid C# identifiers without keywords.");
        if ((Catalog is null) == (SourceL5x is null)) throw new FormatException("Supply exactly one embedded catalog or sourceL5x.");
        var catalog = Catalog;
        if (SourceL5x is not null)
        {
            string path = Path.GetFullPath(SourceL5x, projectDirectory ?? Environment.CurrentDirectory);
            byte[] source = File.ReadAllBytes(path);
            if (SourceSha256 is not null && !Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(source)).Equals(SourceSha256, StringComparison.OrdinalIgnoreCase))
                throw new FormatException("L5X source SHA-256 differs from the pinned contract project.");
            using var input = new MemoryStream(source); catalog = L5xCatalog.Read(input);
        }
        var bundle = Generator.Generate(catalog!, Selections, IncludeImplicit, ModuleName, SoftwareRevision, AssemblyBytes, Explicit, Implicit, Heartbeat,
            scopedMemberTypes ? ClassName : null);
        var config = bundle.Configuration with { Gateway = Gateway, Route = Route, Implicit = IncludeImplicit ? Implicit ?? new() : null };
        config.Validate();
        // Regenerate module references using the final assembly choices. Payload layout stays unchanged.
        string revision = string.IsNullOrEmpty(SoftwareRevision) ? catalog!.SoftwareRevision : SoftwareRevision;
        string model = bundle.ModelCs.Replace(JsonSerializer.Serialize(bundle.Configuration.ToJson()), JsonSerializer.Serialize(config.ToJson()), StringComparison.Ordinal).Replace("namespace LogixTagRelay.Generated;", $"namespace {Namespace};", StringComparison.Ordinal)
            .Replace("class GeneratedTags :", $"class {ClassName} :", StringComparison.Ordinal)
            .Replace("public GeneratedTags()", $"public {ClassName}()", StringComparison.Ordinal);
        return bundle with { ModelCs = model, Configuration = config,
            RoutineL5x = IncludeImplicit ? L5xGenerator.Generate(config, ModuleName, revision) : "",
            ModuleInstructions = bundle.ModuleInstructions.Replace("IP address: the PC NIC serving the adapter (not the PLC IP)", $"IP address: {PcAddress} (PC adapter NIC; not the PLC)", StringComparison.Ordinal) };
    }
    private static readonly HashSet<string> Keywords = new(("abstract as base bool break byte case catch char checked class const continue decimal default delegate do double else enum event explicit extern false finally fixed float for foreach goto if implicit in int interface internal is lock long namespace new null object operator out override params private protected public readonly ref return sbyte sealed short sizeof stackalloc static string struct switch this throw true try typeof uint ulong unchecked unsafe ushort using virtual void volatile while record required file").Split(' '), StringComparer.Ordinal);
}

/// <summary>One contract difference with a path, description and compatibility classification.</summary>
/// <param name="Path">Fully qualified Logix path including optional program scope and array coordinates.</param>
/// <param name="Description">Human-readable explanation of the contract difference.</param>
/// <param name="Breaking">Whether the change breaks the existing model or implicit wire contract.</param>
public sealed record ContractChange(string Path, string Description, bool Breaking);
/// <summary>Compares generated contracts without browsing or writing a PLC.</summary>
public static class ContractComparison
{
    /// <summary>Reports field, layout, access and communication-setting changes; implicit layout changes require both peers to be regenerated.</summary>
    /// <param name="before">Previously generated configuration.</param>
    /// <param name="after">Newly generated configuration.</param>
    public static IReadOnlyList<ContractChange> Compare(SyncConfiguration before, SyncConfiguration after)
    {
        before.Validate(); after.Validate(); var result = new List<ContractChange>();
        var old = before.Fields.ToDictionary(f => f.Path, StringComparer.OrdinalIgnoreCase);
        var next = after.Fields.ToDictionary(f => f.Path, StringComparer.OrdinalIgnoreCase);
        foreach (var f in before.Fields)
            if (!next.TryGetValue(f.Path, out var n)) result.Add(new(f.Path, "Removed", true));
            else if (f != n) result.Add(new(f.Path, "Type, direction, access, capacity or wire offset changed", true));
        foreach (var f in after.Fields.Where(f => !old.ContainsKey(f.Path))) result.Add(new(f.Path, "Added; regenerate both wire peers", before.Implicit is not null || after.Implicit is not null));
        if (before.PlcToAppBytes != after.PlcToAppBytes) result.Add(new("$image", "Assembly length changed", true));
        if (before.Implicit != after.Implicit) result.Add(new("$implicit", "Assembly settings changed", true));
        if (before.Gateway != after.Gateway || before.Route != after.Route) result.Add(new("$connection", "PLC endpoint or route changed", false));
        if (JsonSerializer.Serialize(before.Explicit, SyncConfiguration.Json) != JsonSerializer.Serialize(after.Explicit, SyncConfiguration.Json)) result.Add(new("$explicit", "Explicit scheduling/read groups changed", false));
        if (before.Heartbeat != after.Heartbeat) result.Add(new("$heartbeat", "Heartbeat tag/interval changed", false));
        return result;
    }
}
