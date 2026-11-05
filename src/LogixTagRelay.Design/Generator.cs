using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace LogixTagRelay.Design;

/// <summary>A selected catalog path and requested synchronization direction.</summary>
/// <param name="Path">Fully qualified Logix path including optional program scope and array coordinates.</param>
/// <param name="Direction">Requested synchronization direction.</param>
public sealed record TagSelection(string Path, Direction Direction);
/// <summary>Generated C# contract, matching configuration and PLC import artifacts; imports require native acceptance.</summary>
/// <param name="ModelCs">Generated ordinary C# source for the typed contract.</param>
/// <param name="Configuration">Configuration matching the generated scalar layout.</param>
/// <param name="RoutineL5x">PLC wrapper routine import, or empty when implicit generation is disabled.</param>
/// <param name="ModuleInstructions">Human-readable Generic Ethernet Module setup instructions.</param>
public sealed record GeneratedBundle(string ModelCs, SyncConfiguration Configuration, string RoutineL5x, string ModuleInstructions)
{
    /// <summary>L5X import containing the type-ignorant PLC-first SINT merge AOI.</summary>
    public string MergeAoiL5x { get; init; } = "";
    /// <summary>Structured Text source of the generated type-ignorant merge AOI.</summary>
    public string MergeStructuredText { get; init; } = "";
}

/// <summary>Generates strongly typed C# and PLC artifacts from selected catalog members.</summary>
public static partial class Generator
{
    /// <summary>Validates selections and permissions, then creates matching C#, configuration and optional bounded implicit PLC artifacts.</summary>
    /// <param name="catalog">Source tag/type catalog with access metadata.</param>
    /// <param name="selections">Catalog paths and requested synchronization directions.</param>
    /// <param name="includeImplicit">Whether to emit the bounded implicit image and PLC artifacts.</param>
    /// <param name="moduleName">Generic Ethernet Module identifier, at most 16 characters.</param>
    /// <param name="softwareRevision">Target Studio 5000 software revision; native acceptance is required separately.</param>
    /// <param name="assemblyBytes">Requested symmetric image size, or zero to use the minimum aligned length.</param>
    /// <param name="explicitOptions">Explicit update and DINT control settings, or defaults.</param>
    /// <param name="implicitOptions">Implicit assembly and timing settings, or defaults.</param>
    /// <param name="heartbeatOptions">Optional explicit-only BOOL heartbeat outside the selected data members.</param>
    public static GeneratedBundle Generate(TagCatalog catalog, IReadOnlyList<TagSelection> selections,
        bool includeImplicit = true, string moduleName = "LogixRelay", string softwareRevision = "", int assemblyBytes = 0,
        ExplicitOptions? explicitOptions = null, ImplicitOptions? implicitOptions = null, HeartbeatOptions? heartbeatOptions = null) =>
        Generate(catalog, selections, includeImplicit, moduleName, softwareRevision, assemblyBytes, explicitOptions, implicitOptions, heartbeatOptions, null);

    internal static GeneratedBundle Generate(TagCatalog catalog, IReadOnlyList<TagSelection> selections,
        bool includeImplicit, string moduleName, string softwareRevision, int assemblyBytes, ExplicitOptions? explicitOptions,
        ImplicitOptions? implicitOptions, HeartbeatOptions? heartbeatOptions, string? ownerClass)
    {
        if (!Identifier().IsMatch(moduleName) || moduleName.Length > 16) throw new ArgumentException("Module name must be an identifier of at most 16 characters.");
        var nodes = new List<(TagNode Node, Direction Direction)>();
        var allNodes = Flatten(catalog.Tags).ToDictionary(n => n.Path, StringComparer.OrdinalIgnoreCase);
        foreach (var selection in selections)
        {
            if (!allNodes.TryGetValue(selection.Path, out var node) || !node.Selectable)
                throw new ArgumentException($"Unsupported selection: {selection.Path}");
            nodes.Add((node, selection.Direction));
        }
        if (heartbeatOptions is { } heartbeat)
        {
            if (!allNodes.TryGetValue(heartbeat.Tag, out var node) || node.AtomicType != PlcType.BOOL || node.IsArray || node.Access != "Read/Write")
                throw new ArgumentException("Heartbeat must name an existing scalar, externally writable BOOL in the catalog.");
            if (nodes.SelectMany(n => n.Node.Leaves()).Any(n => n.Path.Equals(heartbeat.Tag, StringComparison.OrdinalIgnoreCase)))
                throw new ArgumentException("Select a heartbeat BOOL outside the contract data selections.");
        }
        var fields = new List<FieldMap>();
        int imageBytes = ImplicitImage.HeaderBytes;
        foreach (var (node, direction) in nodes)
        {
            foreach (var leaf in node.Leaves())
            {
                var type = leaf.AtomicType ?? throw new ArgumentException("Selection has no supported leaf type.");
                var leafDirection = direction == Direction.Bidirectional && leaf.Access == "Read Only" ? Direction.PlcToApp : direction;
                var field = new FieldMap(leaf.Path, type, leafDirection, imageBytes, leaf.Access, leaf.StringCapacity);
                fields.Add(field);
                imageBytes += ((field.WireBytes + 3) / 4 * 4);
            }
        }
        if (assemblyBytes != 0 && (assemblyBytes < imageBytes || assemblyBytes % 4 != 0))
            throw new ArgumentException($"Assembly length must be a multiple of 4 and at least {imageBytes} bytes for this contract.");
        imageBytes = Math.Max(imageBytes, assemblyBytes);
        var readGroups = new List<ExplicitReadGroup>();
        void Group(TagNode node)
        {
            if (node.IsArray) { foreach (var child in node.Children) Group(child); return; }
            if (node.Children.Count == 0 || BuiltInTypes.IsTimerOrCounter(node.DataType)) return;
            // BOOL array leaves use the driver's explicit bit addressing rather than a guessed packed layout.
            var members = node.Leaves().Where(n => !allNodes.Values.Any(b => BuiltInTypes.IsTimerOrCounter(b.DataType) && n.Path.StartsWith(b.Path + ".", StringComparison.OrdinalIgnoreCase))).Where(n => !(n.AtomicType == PlcType.BOOL && n.Path.EndsWith(']'))).Select(n => n.Path).ToList();
            if (members.Count > 0) readGroups.Add(new(node.Path, node.DataType, members));
        }
        foreach (var (node, _) in nodes) Group(node);
        var explicitConfig = explicitOptions ?? new();
        explicitConfig = explicitConfig with { ReadGroups = explicitConfig.ReadGroups ?? readGroups };
        var config = new SyncConfiguration(SyncConfiguration.ComputeHash(fields), fields, imageBytes, imageBytes,
            Explicit: explicitConfig, Implicit: includeImplicit ? implicitOptions ?? new() : null, Heartbeat: heartbeatOptions);
        config.Validate(); // Includes duplicate parent/child selections and incompatible access.
        foreach (var path in new[] { config.Explicit!.TriggerTag, config.Explicit.MirrorTag, config.Explicit.ResponseTag }.Where(p => p != null))
        {
            if (!allNodes.TryGetValue(path!, out var control) || control.IsArray || control.AtomicType != PlcType.DINT || control.Access == "None")
                throw new ArgumentException($"Trigger controls must be existing accessible DINT leaves in the catalog: {path}");
            if (!path!.Equals(config.Explicit.TriggerTag, StringComparison.OrdinalIgnoreCase) && control.Access != "Read/Write")
                throw new ArgumentException($"Reply control must have Read/Write external access: {path}");
        }
        if (includeImplicit)
        {
            string program = fields.Select(f => Regex.Match(f.Path, @"^Program:([^.]+)\."))
                .FirstOrDefault(m => m.Success)?.Groups[1].Value ?? "MainProgram";
            foreach (var field in fields.Where(f => !f.Path.StartsWith("Program:", StringComparison.OrdinalIgnoreCase)))
            {
                string root = field.Path.Split('.', '[')[0];
                if (catalog.Tags.Any(t => t.Path.Equals($"Program:{program}.{root}", StringComparison.OrdinalIgnoreCase)))
                    throw new ArgumentException($"Program {program} shadows controller tag {root}; use a dedicated mapping program or resolve the conflict.");
            }
            if (catalog.Tags.Any(t => t.Path.StartsWith($"Program:{program}.{moduleName}_PS_", StringComparison.OrdinalIgnoreCase)))
                throw new ArgumentException("Generated staging prefix already exists in the target program. Choose a different module/prefix or reconcile the prior import.");
        }
        string model = GenerateModel(nodes, config, ownerClass);
        string revision = string.IsNullOrEmpty(softwareRevision) ? catalog.SoftwareRevision : softwareRevision;
        if (includeImplicit && !Regex.IsMatch(revision, @"^\d{1,2}\.\d{1,2}$"))
            throw new ArgumentException("Specify the target Studio 5000 software revision (for example 35.00) for an implicit L5X import.");
        return new(model, config, includeImplicit ? L5xGenerator.Generate(config, moduleName, revision) : "",
            $"""
            Generic Ethernet Module settings (PLC perspective)
            Name: {moduleName}
            IP address: the PC NIC serving the adapter (not the PLC IP)
            Comm Format: Data - SINT
            Input assembly: {config.Implicit?.PlcInputAssembly ?? 101}; size: {imageBytes} bytes (PC -> PLC)
            Output assembly: {config.Implicit?.PlcOutputAssembly ?? 100}; size: {imageBytes} bytes (PLC -> PC)
            Configuration assembly: {config.Implicit?.ConfigurationAssembly ?? 102}; size: 0
            Separate status connection: disabled (not exposed by this OpENer application)
            RPI: {config.Implicit?.RpiMs ?? 20} ms; actual supported RPI must be negotiated/tested with the chosen adapter.
            Unicast preferred; adapter transport headers are NOT included in these payload sizes.

            Implicit generation includes the routine export. A matching native template optionally supplies a separate module import.
            Without that template, add the module manually using these settings.
            Import into the program containing any selected program-scoped tags. One program scope per group.
            Explicitly call the imported routine from a scheduled task (JSR). It is not automatically scheduled.
            All selected application tags must already exist. Import creates only module-prefixed staging tags.
            The routine embeds the LTR_SintMerge Structured Text AOI; separate AOI L5X/ST are supplied for review.
            LastSent/Current/Received are payload-only SINT arrays. LastSent is saved immediately after module output is written.
            PLC output remains frozen until a new API command references that exact snapshot and completes its byte merge.
            PLC wins each byte changed since LastSent; multi-byte values may combine bytes from both writers.
            Invalid incoming or merged STRING LENs prevent all command copies and acknowledgement.
            Drive {moduleName}_PS_ApplyCommands from application permission logic. Its initial value is FALSE.
            The 1000 ms watchdog reports a lack of fresh matching generations. Repeated packets cannot refresh it.
            Application logic must handle its DN bit and module faults to choose hold/reset behavior for prior commands.
            Hold {moduleName}_PS_Reset while reconciling connection/state, then release it. Managed sessions negotiate a new baseline after reset; recreate standalone codecs.
            Wire protocol 3 requires regeneration of the PLC routine and API together; JSON schema remains version 2.
            Studio 5000 import, verify, export/readback, then PLC bench validation are still required.
            """ + "\n")
        {
            MergeAoiL5x = includeImplicit ? SintMergeAoi.GenerateL5x(revision) : "",
            MergeStructuredText = includeImplicit ? SintMergeAoi.StructuredText : ""
        };
    }
    private static IEnumerable<TagNode> Flatten(IEnumerable<TagNode> nodes) => nodes.SelectMany(n => new[] { n }.Concat(Flatten(n.Children)));
    private static string GenerateModel(List<(TagNode Node, Direction Direction)> nodes, SyncConfiguration config, string? ownerClass)
    {
        var classes = new Dictionary<string, (string Name, TagNode Node)>();
        string Shape(TagNode n) => n.DataType + ":" + n.StringCapacity + (n.IsArray ? "[" + string.Join(",", n.ArrayDimensions) + "]" + Shape(n.Children[0]) : "{" + string.Join(";", n.Children.Select(c => c.Name + ":" + Shape(c))) + "}");
        string Type(TagNode n)
        {
            if (n.IsArray) return Type(n.Children[0]) + "[" + new string(',', n.ArrayDimensions.Length - 1) + "]";
            if (n.Children.Count == 0) return $"PlcVariable<{PlcTypes.CSharp(n.AtomicType!.Value)}>";
            string key = Shape(n);
            if (!classes.TryGetValue(key, out var item))
            {
                string prefix = ownerClass is null ? "Udt" : ownerClass + "_Udt";
                item = ($"{prefix}{classes.Count + 1}", n); classes.Add(key, item);
                foreach (var child in n.Children) _ = Type(child);
            }
            return item.Name;
        }
        string Literal(string s) => JsonSerializer.Serialize(s);
        string Init(TagNode n)
        {
            if (n.IsArray)
            {
                var dims = n.ArrayDimensions;
                if (ArrayShape.Count(dims) != n.Children.Count) throw new ArgumentException("Array shape does not match catalog elements.");
                int index = 0;
                string Initializer(int depth) => "{ " + string.Join(", ", Enumerable.Range(0, dims[depth]).Select(_ =>
                    depth == dims.Length - 1 ? Init(n.Children[index++]) : Initializer(depth + 1))) + " }";
                return "new " + Type(n.Children[0]) + "[" + string.Join(",", dims) + "] " + Initializer(0);
            }
            if (n.Children.Count != 0) return "new " + Type(n) + " { " + string.Join(", ", n.Children.Select(c => Id(c.Name) + " = " + Init(c))) + " }";
            var f = config.Fields.Single(f => f.Path == n.Path);
            return $"new {Type(n)}(new FieldMap({Literal(f.Path)}, PlcType.{f.Type}, Direction.{f.Direction}, {f.Offset}, {Literal(f.Access)}, {f.StringCapacity}))";
        }
        var names = nodes.Select(n => Id(n.Node.Path)).ToArray();
        if (names.Distinct(StringComparer.Ordinal).Count() != names.Length) throw new ArgumentException("C# identifier collision.");
        string Doc(string text) => System.Security.SecurityElement.Escape(text);
        string[] Lines(string text) => text.ReplaceLineEndings("\n").Split('\n');
        var builder = new StringBuilder("// <auto-generated/>\n#nullable enable\nusing System.Collections.Generic;\nusing LogixTagRelay;\nnamespace LogixTagRelay.Generated;\n\n/// <summary>Generated PLC contract with fixed membership and change-only value notifications.</summary>\npublic sealed partial class GeneratedTags : PlcModel\n{\n");
        string memberIndent = "    ";
        void Summary(string text)
        {
            var lines = Lines(text);
            if (lines.Length == 1) builder.AppendLine($"{memberIndent}/// <summary>{Doc(text)}</summary>");
            else
            {
                builder.AppendLine($"{memberIndent}/// <summary>");
                foreach (string line in lines) builder.AppendLine($"{memberIndent}/// <para>{Doc(line)}</para>");
                builder.AppendLine($"{memberIndent}/// </summary>");
            }
        }
        void Remarks(IEnumerable<TagNode> members)
        {
            var groups = members.Where(n => !string.IsNullOrWhiteSpace(n.Description)).GroupBy(n => n.Description, StringComparer.Ordinal).ToArray();
            if (groups.Length == 0) return;
            builder.AppendLine($"{memberIndent}/// <remarks>");
            foreach (var group in groups)
            {
                var paths = group.Select(n => n.Path).Distinct(StringComparer.Ordinal).ToArray();
                string label = string.Join(", ", paths.Take(8)) + (paths.Length > 8 ? $" (and {paths.Length - 8} more)" : "");
                builder.AppendLine($"{memberIndent}/// <para><c>{Doc(label)}</c></para>");
                foreach (string line in Lines(group.Key!)) builder.AppendLine($"{memberIndent}/// <para>{Doc(line)}</para>");
            }
            builder.AppendLine($"{memberIndent}/// </remarks>");
        }
        void Describe(IReadOnlyList<TagNode> members, string fallback)
        {
            var descriptions = members.Select(n => n.Description).Distinct(StringComparer.Ordinal).ToArray();
            Summary(descriptions.Length == 1 && !string.IsNullOrWhiteSpace(descriptions[0]) ? descriptions[0]! : fallback);
            if (descriptions.Length > 1) Remarks(members);
            Remarks(members.Where(n => n.IsArray).SelectMany(n => n.Children.Where(c => c.Description != n.Description)));
        }
        for (int i = 0; i < nodes.Count; i++)
        {
            Describe([nodes[i].Node], "PLC tag " + nodes[i].Node.Path + " (" + nodes[i].Direction + ").");
            builder.AppendLine($"    public {Type(nodes[i].Node)} {names[i]} {{ get; }} = {Init(nodes[i].Node)};");
        }
        var refs = new List<string>();
        void References(TagNode node, string expression)
        {
            if (node.Children.Count == 0) refs.Add(expression);
            else for (int i = 0; i < node.Children.Count; i++)
                References(node.Children[i], node.IsArray ? $"{expression}[{ArrayShape.Subscript(i, node.ArrayDimensions)}]" : expression + "." + Id(node.Children[i].Name));
        }
        for (int i = 0; i < nodes.Count; i++) References(nodes[i].Node, names[i]);
        builder.AppendLine("    /// <summary>Creates a fresh configuration matching this generated model and its wire layout.</summary>");
        builder.AppendLine("    public static SyncConfiguration CreateConfiguration() => SyncConfiguration.FromJson(" + Literal(config.ToJson()) + ");");
        var roots = nodes.SelectMany((n, i) => n.Node.Leaves().Select(_ => Literal(names[i])));
        builder.AppendLine("    /// <summary>Creates the contract and connects member change notifications.</summary>");
        builder.Append("    public GeneratedTags() => InitializeVariables(new IPlcVariable[] { " + string.Join(", ", refs) + " }, new string[] { " + string.Join(", ", roots) + " });");
        if (ownerClass is null) builder.AppendLine("\n}");
        else builder.AppendLine();
        string typeIndent = ownerClass is null ? "" : "    ";
        memberIndent = typeIndent + "    ";
        var instances = Flatten(nodes.Select(n => n.Node)).Where(n => !n.IsArray && n.Children.Count > 0).ToLookup(Shape);
        foreach (var (shape, item) in classes)
        {
            builder.AppendLine($"\n{typeIndent}/// <summary>{Doc("Generated member group for Logix type " + item.Node.DataType + ".")}</summary>\n{typeIndent}public sealed class {item.Name}\n{typeIndent}{{");
            for (int i = 0; i < item.Node.Children.Count; i++)
            {
                var child = item.Node.Children[i];
                Describe(instances[shape].Select(n => n.Children[i]).ToArray(), "PLC member " + child.Name + " (" + child.DataType + ").");
                builder.AppendLine($"{memberIndent}public {Type(child)} {Id(child.Name)} {{ get; init; }} = null!;");
            }
            builder.AppendLine(typeIndent + "}");
        }
        if (ownerClass is not null) builder.AppendLine("}");
        return builder.ToString();
    }
    // Encode punctuation and underscore so sanitized paths cannot collide. Prefix avoids keywords.
    private static string Id(string name) => "Tag_" + string.Concat(name.Select(c => char.IsAsciiLetterOrDigit(c) ? c.ToString() : "_" + ((int)c).ToString("X4", CultureInfo.InvariantCulture)));
    [GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_]*$")] private static partial Regex Identifier();
}
