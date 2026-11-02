using System.Xml;
using System.Xml.Linq;

namespace LogixTagRelay.Design;

public sealed record TagNode(string Name, string Path, string DataType, string Access,
    List<TagNode> Children, bool IsArray = false, string? UnsupportedReason = null)
{
    public IEnumerable<TagNode> Leaves() => Children.Count == 0 ? [this] : Children.SelectMany(c => c.Leaves());
    public bool Selectable => Leaves().All(n => n.UnsupportedReason is null && Enum.TryParse<PlcType>(n.DataType, out _));
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
        TagNode Build(string name, string path, string type, string dimensions, string access, HashSet<string> ancestors, int depth)
        {
            if (++expanded > 20000 || depth > 32) throw new FormatException("Catalog expansion limit exceeded.");
            var dims = dimensions.Split([',', ' '], StringSplitOptions.RemoveEmptyEntries).Select(int.Parse).Where(x => x != 0).ToArray();
            if (dims.Length > 0)
            {
                if (dims.Length != 1 || dims[0] < 1 || dims[0] > 4096)
                    return new(name, path, type, access, [], true, "Only one-dimensional arrays of 1..4096 elements are supported in this preview.");
                return new(name, path, type, access, Enumerable.Range(0, dims[0])
                    .Select(i => Build($"Element{i}", $"{path}[{i}]", type, "0", access, ancestors, depth + 1)).ToList(), true);
            }
            if (Enum.TryParse<PlcType>(type, out var parsed) && Enum.IsDefined(parsed))
                return new(name, path, type, access, [], false, access == "None" ? "ExternalAccess=None" : null);
            if (!types.TryGetValue(type, out var definition)) return new(name, path, type, access, [], false, $"Unsupported or missing type: {type}");
            if (!ancestors.Add(type)) throw new FormatException("Recursive UDT definition.");
            var children = new List<TagNode>();
            foreach (var member in definition.Element("Members")?.Elements("Member") ?? [])
            {
                if ((string?)member.Attribute("Hidden") == "true") continue;
                string memberName = Required(member, "Name");
                // Logix exports BOOL members as either BOOL or BIT with a hidden backing member.
                string memberType = Required(member, "DataType");
                if (memberType == "BIT") memberType = "BOOL";
                children.Add(Build(memberName, path + "." + memberName, memberType,
                    (string?)member.Attribute("Dimension") ?? "0", Restrict(access, (string?)member.Attribute("ExternalAccess") ?? "Read/Write"),
                    new(ancestors, StringComparer.OrdinalIgnoreCase), depth + 1));
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
}
