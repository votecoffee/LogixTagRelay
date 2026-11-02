using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace LogixTagRelay.Design;

public sealed record ModuleTemplateInfo(string SourceSha256, string Name, string SoftwareRevision,
    string Address, int InputAssembly, int OutputAssembly, int? ConfigurationAssembly,
    int InputBytes, int OutputBytes, int ConfigurationBytes, int StatusConnections,
    string CommMethod, List<string> Errors);
public sealed record ModuleImport(string Xml, string SourceSha256);

/// <summary>Generates a review copy from a matching native export. Does not claim native import acceptance.</summary>
public static partial class ModuleTemplate
{
    public static ModuleTemplateInfo Inspect(byte[] bytes, ImplicitOptions? settings = null)
    {
        var doc = Read(bytes);
        var module = Target(doc);
        var communications = module.Element("Communications") ?? throw new FormatException("Missing module Communications.");
        var connections = communications.Element("Connections")?.Elements("Connection").ToArray() ?? [];
        var primary = connections.SingleOrDefault(c => (string?)c.Attribute("Name") == "Standard")
            ?? throw new FormatException("Expected one Standard connection.");
        var configTag = communications.Element("ConfigTag") ?? throw new FormatException("Missing ConfigTag.");
        var match = ConfigPrefix().Match(configTag.Elements("Data").SingleOrDefault(d => (string?)d.Attribute("Format") == "L5K")?.Value ?? "");
        int? configAssembly = match.Success ? int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture) : null;
        int input = Number(primary, "InputSize"), output = Number(primary, "OutputSize"), configSize = Number(configTag, "ConfigSize");
        int inputPoint = Number(primary, "InputCxnPoint"), outputPoint = Number(primary, "OutputCxnPoint");
        var errors = new List<string>();
        settings ??= new();
        if (inputPoint != settings.PlcInputAssembly || outputPoint != settings.PlcOutputAssembly || configAssembly != settings.ConfigurationAssembly)
            errors.Add($"Assembly instances must match this adapter: input {settings.PlcInputAssembly}, output {settings.PlcOutputAssembly}, configuration {settings.ConfigurationAssembly}.");
        if ((string?)module.Attribute("CatalogNumber") != "ETHERNET-MODULE") errors.Add("Expected a Generic Ethernet Module export.");
        if (connections.Length != 1) errors.Add("Disable the separate status connection; this adapter exposes one exclusive-owner data connection.");
        if (input != output || input is < 24 or > 480 || input % 4 != 0) errors.Add("Input/output sizes must match, be divisible by 4, and be within 24..480 bytes.");
        if (configSize != 0) errors.Add("Configuration size must be zero.");
        if (configAssembly == null) errors.Add("Configuration instance could not be established from this native export format.");
        if (Number(communications, "PrimCxnInputSize") != input || Number(communications, "PrimCxnOutputSize") != output)
            errors.Add("Primary connection sizes disagree with module metadata.");
        foreach (var (tagName, size) in new[] { ("InputTag", input), ("OutputTag", output) })
        {
            var structure = primary.Element(tagName)?.Elements("Data").SingleOrDefault(d => (string?)d.Attribute("Format") == "Decorated")?.Element("Structure");
            var data = structure?.Element("ArrayMember");
            if (data == null || (string?)data.Attribute("Name") != "Data" || (string?)data.Attribute("DataType") != "SINT" ||
                Number(data, "Dimensions") != size || !(structure!.Attribute("DataType")?.Value.Contains("_SINT_", StringComparison.Ordinal) ?? false))
                errors.Add($"{tagName} must be native decorated SINT data with the declared size.");
        }
        return new(Convert.ToHexString(SHA256.HashData(bytes)), (string?)module.Attribute("Name") ?? "",
            (string?)doc.Root!.Attribute("SoftwareRevision") ?? "", (string?)module.Element("Ports")?.Element("Port")?.Attribute("Address") ?? "",
            inputPoint, outputPoint, configAssembly, input, output, configSize,
            connections.Count(c => (string?)c.Attribute("Name") == "Status"), (string?)communications.Attribute("CommMethod") ?? "", errors);
    }
    public static ModuleImport Generate(byte[] template, SyncConfiguration config, string moduleName, string pcAddress)
    {
        config.Validate();
        var io = config.Implicit ?? throw new ArgumentException("Implicit settings are required.");
        var info = Inspect(template, io);
        if (info.Errors.Count > 0) throw new ArgumentException("Native module template needs correction: " + string.Join(" ", info.Errors));
        if (info.InputAssembly != io.PlcInputAssembly || info.OutputAssembly != io.PlcOutputAssembly || info.ConfigurationAssembly != io.ConfigurationAssembly)
            throw new ArgumentException("Native module assembly instances must match the adapter settings. Re-export with the requested instances.");
        if (!Regex.IsMatch(moduleName, @"^[A-Za-z_][A-Za-z0-9_]{0,15}$") || !System.Net.IPAddress.TryParse(pcAddress, out var address) ||
            address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork) throw new ArgumentException("Provide a valid module name and PC IPv4 address.");
        var doc = Read(template);
        var module = Target(doc);
        doc.Root!.SetAttributeValue("TargetName", moduleName);
        module.SetAttributeValue("Name", moduleName);
        module.Element("Ports")!.Element("Port")!.SetAttributeValue("Address", pcAddress);
        var communications = module.Element("Communications")!;
        int size = config.AppToPlcBytes;
        communications.SetAttributeValue("PrimCxnInputSize", size);
        communications.SetAttributeValue("PrimCxnOutputSize", size);
        var primary = communications.Element("Connections")!.Elements("Connection").Single();
        primary.SetAttributeValue("InputSize", size); primary.SetAttributeValue("OutputSize", size);
        primary.SetAttributeValue("RPI", (long)io.RpiMs * 1000); primary.SetAttributeValue("Unicast", "true");
        foreach (string tagName in new[] { "InputTag", "OutputTag" })
        {
            var tag = primary.Element(tagName)!;
            var structure = tag.Elements("Data").Single(d => (string?)d.Attribute("Format") == "Decorated").Element("Structure")!;
            structure.SetAttributeValue("DataType", Regex.Replace((string)structure.Attribute("DataType")!, @"_SINT_\d+Bytes:", $"_SINT_{size}Bytes:"));
            var array = structure.Element("ArrayMember")!;
            array.SetAttributeValue("Dimensions", size);
            array.ReplaceNodes(Enumerable.Range(0, size).Select(i => new XElement("Element", new XAttribute("Index", $"[{i}]"), new XAttribute("Value", "0"))));
            foreach (var raw in tag.Elements("Data").Where(d => (string?)d.Attribute("Format") == "L5K"))
                raw.ReplaceNodes(new XCData("[[" + string.Join(",", Enumerable.Repeat("0", size)) + "]]"));
        }
        // CommMethod, ConfigTag (including its opaque L5K prefix), keying, and parent-module routing stay source-bound.
        using var stream = new MemoryStream();
        using (var writer = XmlWriter.Create(stream, new XmlWriterSettings { Encoding = new UTF8Encoding(false), Indent = false, NewLineChars = "\r\n" })) doc.Save(writer);
        return new(Encoding.UTF8.GetString(stream.ToArray()), info.SourceSha256);
    }
    private static XDocument Read(byte[] bytes)
    {
        if (bytes.Length > 16 * 1024 * 1024) throw new FormatException("Module template exceeds 16 MiB.");
        using var stream = new MemoryStream(bytes, false);
        using var reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 16 * 1024 * 1024 });
        var doc = XDocument.Load(reader, LoadOptions.PreserveWhitespace);
        if (doc.Root?.Name != "RSLogix5000Content" || (string?)doc.Root.Attribute("TargetType") != "Module") throw new FormatException("Expected an L5X Module export.");
        return doc;
    }
    private static XElement Target(XDocument doc) => doc.Root!.Element("Controller")?.Element("Modules")?.Elements("Module").Single(m => (string?)m.Attribute("Use") == "Target")
        ?? throw new FormatException("Missing target Module.");
    private static int Number(XElement element, string name) => int.TryParse((string?)element.Attribute(name), NumberStyles.Integer, CultureInfo.InvariantCulture, out int value)
        ? value : throw new FormatException($"Missing/invalid {element.Name}/@{name}.");
    [GeneratedRegex(@"^\s*\[\s*5\s*,\s*(\d+)\s*,\s*\[", RegexOptions.CultureInvariant)]
    private static partial Regex ConfigPrefix();
}
