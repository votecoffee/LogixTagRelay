using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace LogixTagRelay.Design;

public sealed record TagSelection(string Path, Direction Direction);
public sealed record GeneratedBundle(string ModelCs, SyncConfiguration Configuration, string RoutineL5x, string ModuleInstructions)
{
    public string MergeAoiL5x { get; init; } = "";
    public string MergeStructuredText { get; init; } = "";
}

public static partial class Generator
{
    public static GeneratedBundle Generate(TagCatalog catalog, IReadOnlyList<TagSelection> selections,
        bool includeImplicit = true, string moduleName = "LogixRelay", string softwareRevision = "", int assemblyBytes = 0,
        ExplicitOptions? explicitOptions = null, ImplicitOptions? implicitOptions = null)
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
            Explicit: explicitConfig, Implicit: includeImplicit ? implicitOptions ?? new() : null);
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
        string model = GenerateModel(nodes, config);
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
    private static string GenerateModel(List<(TagNode Node, Direction Direction)> nodes, SyncConfiguration config)
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
                item = ($"Udt{classes.Count + 1}", n); classes.Add(key, item);
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
        var builder = new StringBuilder("// <auto-generated/>\n#nullable enable\nusing System.Collections.Generic;\nusing LogixTagRelay;\nnamespace LogixTagRelay.Generated;\n\npublic sealed partial class GeneratedTags : PlcModel\n{\n");
        for (int i = 0; i < nodes.Count; i++) builder.AppendLine($"    public {Type(nodes[i].Node)} {names[i]} {{ get; }} = {Init(nodes[i].Node)};");
        var refs = new List<string>();
        void References(TagNode node, string expression)
        {
            if (node.Children.Count == 0) refs.Add(expression);
            else for (int i = 0; i < node.Children.Count; i++)
                References(node.Children[i], node.IsArray ? $"{expression}[{ArrayShape.Subscript(i, node.ArrayDimensions)}]" : expression + "." + Id(node.Children[i].Name));
        }
        for (int i = 0; i < nodes.Count; i++) References(nodes[i].Node, names[i]);
        builder.AppendLine("    public static SyncConfiguration CreateConfiguration() => SyncConfiguration.FromJson(" + Literal(config.ToJson()) + ");");
        var roots = nodes.SelectMany((n, i) => n.Node.Leaves().Select(_ => Literal(names[i])));
        builder.AppendLine("    public GeneratedTags() => InitializeVariables(new IPlcVariable[] { " + string.Join(", ", refs) + " }, new string[] { " + string.Join(", ", roots) + " });\n}");
        foreach (var (_, item) in classes)
        {
            builder.AppendLine($"\npublic sealed class {item.Name}\n{{");
            foreach (var child in item.Node.Children) builder.AppendLine($"    public {Type(child)} {Id(child.Name)} {{ get; init; }} = null!;");
            builder.AppendLine("}");
        }
        return builder.ToString();
    }
    // Encode punctuation and underscore so sanitized paths cannot collide. Prefix avoids keywords.
    private static string Id(string name) => "Tag_" + string.Concat(name.Select(c => char.IsAsciiLetterOrDigit(c) ? c.ToString() : "_" + ((int)c).ToString("X4", CultureInfo.InvariantCulture)));
    [GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_]*$")] private static partial Regex Identifier();
}
