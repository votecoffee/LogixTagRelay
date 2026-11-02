using System.Text.Json;
using System.Text.RegularExpressions;

namespace LogixTagRelay.Design;

/// <summary>Portable, versioned generation inputs. Catalogs can be embedded or loaded from a relative L5X.</summary>
public sealed record ContractProject(string Name, List<TagSelection> Selections, TagCatalog? Catalog = null,
    string? SourceL5x = null, string? SourceSha256 = null, string Namespace = "LogixTagRelay.Generated",
    string ClassName = "GeneratedTags", bool IncludeImplicit = true, string ModuleName = "LogixRelay",
    string SoftwareRevision = "", int AssemblyBytes = 0, ExplicitOptions? Explicit = null,
    ImplicitOptions? Implicit = null, string Gateway = "192.168.1.10", string Route = "1,0", int Version = 1)
{
    public string ToJson() => JsonSerializer.Serialize(this, SyncConfiguration.Json) + "\n";
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
    public GeneratedBundle Generate(string? projectDirectory = null)
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
        var bundle = Generator.Generate(catalog!, Selections, IncludeImplicit, ModuleName, SoftwareRevision, AssemblyBytes, Explicit, Implicit);
        var config = bundle.Configuration with { Gateway = Gateway, Route = Route, Implicit = IncludeImplicit ? Implicit ?? new() : null };
        config.Validate();
        // Regenerate module references using the final assembly choices. Payload layout stays unchanged.
        string revision = string.IsNullOrEmpty(SoftwareRevision) ? catalog!.SoftwareRevision : SoftwareRevision;
        string model = bundle.ModelCs.Replace("namespace LogixTagRelay.Generated;", $"namespace {Namespace};", StringComparison.Ordinal)
            .Replace("class GeneratedTags :", $"class {ClassName} :", StringComparison.Ordinal)
            .Replace("public GeneratedTags()", $"public {ClassName}()", StringComparison.Ordinal);
        return bundle with { ModelCs = model, Configuration = config,
            RoutineL5x = IncludeImplicit ? L5xGenerator.Generate(config, ModuleName, revision) : "",
            ModuleInstructions = bundle.ModuleInstructions };
    }
    private static readonly HashSet<string> Keywords = new(("abstract as base bool break byte case catch char checked class const continue decimal default delegate do double else enum event explicit extern false finally fixed float for foreach goto if implicit in int interface internal is lock long namespace new null object operator out override params private protected public readonly ref return sbyte sealed short sizeof stackalloc static string struct switch this throw true try typeof uint ulong unchecked unsafe ushort using virtual void volatile while record required file").Split(' '), StringComparer.Ordinal);
}

public sealed record ContractChange(string Path, string Description, bool Breaking);
public static class ContractComparison
{
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
        return result;
    }
}
