using System.Xml;
using System.Xml.Linq;
using System.Text.Json.Serialization;

namespace LogixTagRelay.Design;

public sealed record TagNode(string Name, string Path, string DataType, string Access,
    List<TagNode> Children, bool IsArray = false, string? UnsupportedReason = null, int StringCapacity = 0, int[]? Dimensions = null)
{
    public IEnumerable<TagNode> Leaves() => Children.Count == 0 ? [this] : Children.SelectMany(c => c.Leaves());
    [JsonIgnore] public PlcType? AtomicType => StringCapacity > 0 ? PlcType.STRING :
        Enum.TryParse<PlcType>(DataType, out var type) && Enum.IsDefined(type) && type != PlcType.STRING ? type : null;
    [JsonIgnore] public int[] ArrayDimensions => Dimensions ?? [Children.Count];
    public bool Selectable => Leaves().All(n => n.UnsupportedReason is null && n.AtomicType.HasValue);
}
public sealed record TagCatalog(string Controller, string SoftwareRevision, List<TagNode> Tags);

/// <summary>Offline browser catalog; no controller connection or native layout inference.</summary>
public static class L5xCatalog
{
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
        TagNode Build(string name, string path, string type, string dimensions, string access, HashSet<string> ancestors, int depth, bool isMember = false)
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
                    new(ancestors, StringComparer.OrdinalIgnoreCase), depth + 1, true));
            }
            return new(name, path, type, access, children, false, children.Count == 0 ? "Empty UDT." : null);
        }
        void AddTags(XElement? tags, string prefix, string displayPrefix)
        {
            foreach (var tag in tags?.Elements("Tag") ?? [])
            {
                string name = Required(tag, "Name");
                if ((string?)tag.Attribute("TagType") is { } tagType && tagType != "Base")
                { result.Add(new(displayPrefix + name, prefix + name, "Alias", "None", [], false, "Alias resolution is not implemented.")); continue; }
                result.Add(Build(displayPrefix + name, prefix + name, Required(tag, "DataType"), (string?)tag.Attribute("Dimensions") ?? "0",
                    (string?)tag.Attribute("ExternalAccess") ?? "Read/Write", new(StringComparer.OrdinalIgnoreCase), 0));
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
