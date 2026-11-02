using System.Xml;
using System.Xml.Linq;
using System.Text.Json.Serialization;

namespace LogixTagRelay.Design;

/// <summary>Catalog member with nested UDT or array children, native access metadata and optional unsupported reason.</summary>
/// <param name="Name">Tag, controller or module display name from the source catalog.</param>
/// <param name="Path">Fully qualified Logix path including optional program scope and array coordinates.</param>
/// <param name="DataType">Native Logix scalar or composite type name.</param>
/// <param name="Access">Controller external access metadata, such as Read/Write or Read Only.</param>
/// <param name="Children">Ordered nested UDT members or array elements.</param>
/// <param name="IsArray">Whether this node represents a Logix array.</param>
/// <param name="UnsupportedReason">Reason this node cannot be selected, or null when supported.</param>
/// <param name="StringCapacity">Declared STRING DATA capacity, or zero for other scalar types.</param>
/// <param name="Dimensions">Native array dimensions, when known.</param>
public sealed record TagNode(string Name, string Path, string DataType, string Access,
    List<TagNode> Children, bool IsArray = false, string? UnsupportedReason = null, int StringCapacity = 0, int[]? Dimensions = null)
{
    /// <summary>Effective tag or operand comment, falling back to the UDT member or data-type description when available.</summary>
    public string? Description { get; init; }
    /// <summary>Enumerates scalar descendants in catalog order, or this node when it has no children.</summary>
    public IEnumerable<TagNode> Leaves() => Children.Count == 0 ? [this] : Children.SelectMany(c => c.Leaves());
    /// <summary>Supported scalar type, or null for a composite or unsupported native type.</summary>
    [JsonIgnore] public PlcType? AtomicType => StringCapacity > 0 ? PlcType.STRING :
        Enum.TryParse<PlcType>(DataType, out var type) && Enum.IsDefined(type) && type != PlcType.STRING ? type : null;
    /// <summary>Declared array dimensions, falling back to one dimension based on child count.</summary>
    [JsonIgnore] public int[] ArrayDimensions => Dimensions ?? [Children.Count];
    /// <summary>Whether every scalar descendant has a supported type and no rejection reason.</summary>
    public bool Selectable => Leaves().All(n => n.UnsupportedReason is null && n.AtomicType.HasValue);
}
/// <summary>Controller identity and tag tree from an offline export or live browse.</summary>
/// <param name="Controller">Controller identity from the catalog source.</param>
/// <param name="SoftwareRevision">Studio 5000 revision associated with the source export.</param>
/// <param name="Tags">Ordered catalog or memory tag entries.</param>
public sealed record TagCatalog(string Controller, string SoftwareRevision, List<TagNode> Tags);

/// <summary>Offline browser catalog; no controller connection or native layout inference.</summary>
public static class L5xCatalog
{
    /// <summary>Reads an L5X catalog with DTDs and external entities prohibited and a bounded document size; does not infer native UDT offsets.</summary>
    /// <param name="stream">L5X XML input; external entities and DTDs are not permitted.</param>
    public static TagCatalog Read(Stream stream)
    {
        using var reader = XmlReader.Create(stream, new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 16 * 1024 * 1024
        });
        var doc = XDocument.Load(reader);
        if (doc.Root?.Name != "RSLogix5000Content") throw new FormatException("Expected an L5X export.");
        var controller = doc.Root.Element("Controller") ?? throw new FormatException("Missing Controller.");
        var types = (controller.Element("DataTypes")?.Elements("DataType") ?? [])
            .ToDictionary(e => Required(e, "Name"), StringComparer.OrdinalIgnoreCase);
        var result = new List<TagNode>();
        int expanded = 0;
        string? Text(XElement? element)
        {
            if (element is null) return null;
            string language = (string?)doc.Root.Attribute("CurrentLanguage") ?? "en-US";
            var localized = element.Elements().FirstOrDefault(e => string.Equals((string?)e.Attribute("Lang"), language, StringComparison.OrdinalIgnoreCase))
                ?? element.Elements().FirstOrDefault();
            string value = (localized ?? element).Value.Trim();
            return string.IsNullOrWhiteSpace(value) ? null : value;
        }
        TagNode Build(string name, string path, string type, string dimensions, string access, HashSet<string> ancestors, int depth, bool isMember = false, string? description = null)
        {
            var node = BuildCore(name, path, type, dimensions, access, ancestors, depth, isMember);
            string? effective = description ?? (types.TryGetValue(type, out var definition) ? Text(definition.Element("Description")) : null);
            return node with
            {
                Description = effective,
                Children = node.IsArray ? node.Children.Select(c => c with { Description = c.Description ?? effective }).ToList() : node.Children
            };
        }
        TagNode BuildCore(string name, string path, string type, string dimensions, string access, HashSet<string> ancestors, int depth, bool isMember)
        {
            if (++expanded > 20000 || depth > 32) throw new FormatException("Catalog expansion limit exceeded.");
            var dims = ParseDimensions(dimensions);
            if (dims.Length > 0)
            {
                if (isMember && dims.Length > 1)
                    return new(name, path, type, access, [], true, "Logix UDT array members support one dimension. Define multidimensional arrays as controller/program tags.", Dimensions: dims);
                int count = ArrayShape.Count(dims);
                return new(name, path, type, access, Enumerable.Range(0, count)
                    .Select(i => Build($"[{ArrayShape.Subscript(i, dims)}]", $"{path}[{ArrayShape.Subscript(i, dims)}]", type, "0", access, new(ancestors, StringComparer.OrdinalIgnoreCase), depth + 1)).ToList(), true, Dimensions: dims);
            }
            if (BuiltInTypes.IsTimerOrCounter(type) && !types.ContainsKey(type)) return BuiltInTypes.Expand(name, path, type, access);
            if (type == "STRING" && !types.ContainsKey(type))
                return new(name, path, type, access, [], false, access == "None" ? "ExternalAccess=None" : null, 82);
            if (Enum.TryParse<PlcType>(type, out var parsed) && Enum.IsDefined(parsed) && parsed != PlcType.STRING)
                return new(name, path, type, access, [], false, access == "None" ? "ExternalAccess=None" : null);
            if (!types.TryGetValue(type, out var definition)) return new(name, path, type, access, [], false, $"Unsupported or missing type: {type}");
            var members = (definition.Element("Members")?.Elements("Member") ?? []).Where(m => (string?)m.Attribute("Hidden") != "true").ToArray();
            var len = members.SingleOrDefault(m => (string?)m.Attribute("Name") == "LEN" && (string?)m.Attribute("DataType") == "DINT" && ParseDimensions((string?)m.Attribute("Dimension") ?? "0").Length == 0);
            var data = members.SingleOrDefault(m => (string?)m.Attribute("Name") == "DATA" && (string?)m.Attribute("DataType") == "SINT");
            if (members.Length == 2 && len != null && data != null && members[0] == len && members[1] == data)
            {
                var dataDims = ParseDimensions((string?)data.Attribute("Dimension") ?? "0");
                if (dataDims.Length == 1 && dataDims[0] is >= 1 and <= 65535)
                {
                    string stringAccess = Restrict(Restrict(access, (string?)len.Attribute("ExternalAccess") ?? "Read/Write"), (string?)data.Attribute("ExternalAccess") ?? "Read/Write");
                    return new(name, path, type, stringAccess, [], false, stringAccess == "None" ? "ExternalAccess=None" : null, dataDims[0]);
                }
            }
            if ((string?)definition.Attribute("Family") == "StringFamily")
                return new(name, path, type, access, [], false, "Malformed string type: expected DINT LEN and one-dimensional SINT DATA.");
            if (!ancestors.Add(type)) throw new FormatException("Recursive UDT definition.");
            var children = new List<TagNode>();
            foreach (var member in members)
            {
                if ((string?)member.Attribute("Hidden") == "true") continue;
                string memberName = Required(member, "Name");
                // Logix exports BOOL members as either BOOL or BIT with a hidden backing member.
                string memberType = Required(member, "DataType");
                if (memberType == "BIT") memberType = "BOOL";
                children.Add(Build(memberName, path + "." + memberName, memberType,
                    (string?)member.Attribute("Dimension") ?? "0", Restrict(access, (string?)member.Attribute("ExternalAccess") ?? "Read/Write"),
                    new(ancestors, StringComparer.OrdinalIgnoreCase), depth + 1, true, Text(member.Element("Description"))));
            }
            return new(name, path, type, access, children, false, children.Count == 0 ? "Empty UDT." : null);
        }
        void AddTags(XElement? tags, string prefix, string displayPrefix)
        {
            foreach (var tag in tags?.Elements("Tag") ?? [])
            {
                string name = Required(tag, "Name");
                if ((string?)tag.Attribute("TagType") is { } tagType && tagType != "Base")
                { result.Add(new(displayPrefix + name, prefix + name, "Alias", "None", [], false, "Alias resolution is not implemented.") { Description = Text(tag.Element("Description")) }); continue; }
                var node = Build(displayPrefix + name, prefix + name, Required(tag, "DataType"), (string?)tag.Attribute("Dimensions") ?? "0",
                    (string?)tag.Attribute("ExternalAccess") ?? "Read/Write", new(StringComparer.OrdinalIgnoreCase), 0, description: Text(tag.Element("Description")));
                var comments = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var comment in tag.Element("Comments")?.Elements("Comment") ?? [])
                {
                    string operand = Required(comment, "Operand");
                    if (Text(comment) is not { } text) continue;
                    if (!comments.TryAdd(operand, text)) throw new FormatException("Duplicate tag operand comment: " + name + operand);
                }
                TagNode Annotate(TagNode current) => current with
                {
                    Description = comments.GetValueOrDefault(current.Path[node.Path.Length..]) ?? current.Description,
                    Children = current.Children.Select(Annotate).ToList()
                };
                result.Add(Annotate(node));
            }
        }
        AddTags(controller.Element("Tags"), "", "");
        foreach (var program in controller.Element("Programs")?.Elements("Program") ?? [])
        {
            string name = Required(program, "Name");
            AddTags(program.Element("Tags"), $"Program:{name}.", $"{name}_");
        }
        return new(Required(controller, "Name"), (string?)doc.Root.Attribute("SoftwareRevision") ?? "unknown", result);
    }
    private static string Required(XElement element, string attribute) => (string?)element.Attribute(attribute)
        ?? throw new FormatException($"Missing {element.Name}/@{attribute}.");
    private static string Restrict(string parent, string member) => parent == "None" || member == "None" ? "None"
        : parent == "Read Only" || member == "Read Only" ? "Read Only" : "Read/Write";
    internal static int[] ParseDimensions(string text)
    {
        var dims = text.Split([',', ' '], StringSplitOptions.RemoveEmptyEntries).Select(int.Parse).ToList();
        while (dims.Count > 0 && dims[^1] == 0) dims.RemoveAt(dims.Count - 1);
        if (dims.Count > 3 || dims.Any(d => d < 1)) throw new FormatException("Invalid Logix dimensions; expected 1..3 positive dimensions with optional trailing zero placeholders.");
        return dims.ToArray();
    }
}
