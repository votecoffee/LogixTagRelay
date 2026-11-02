using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace LogixTagRelay.Design;

public static class SintMergeAoi
{
    public const string Name = "LTR_SintMerge";
    public static string StructuredText
    {
        get
        {
            using var stream = typeof(SintMergeAoi).Assembly.GetManifestResourceStream("LogixTagRelay.Design.LTR_SintMerge.st")
                ?? throw new InvalidOperationException("Missing embedded merge AOI source.");
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd().ReplaceLineEndings("\n");
        }
    }
    public static string GenerateL5x(string softwareRevision = "35.03") => Serialize(new XDocument(
        new XDeclaration("1.0", "UTF-8", "yes"),
        new XElement("RSLogix5000Content", new XAttribute("SchemaRevision", "1.0"), new XAttribute("SoftwareRevision", softwareRevision),
            new XAttribute("TargetName", Name), new XAttribute("TargetType", "AddOnInstructionDefinition"), new XAttribute("ContainsContext", "true"),
            new XAttribute("ExportOptions", "References NoRawData DecoratedData Context Dependencies"),
            new XElement("Controller", new XAttribute("Use", "Context"), new XAttribute("Name", "LogixRelayContext"),
                new XElement("DataTypes"), new XElement("AddOnInstructionDefinitions", new XAttribute("Use", "Context"), Definition(softwareRevision, "Target"))))));

    internal static XElement Definition(string softwareRevision, string use = "Context")
    {
        if (!Regex.IsMatch(softwareRevision, @"^\d{1,2}\.\d{1,2}$")) throw new ArgumentException("Specify the target Studio software revision.");
        XElement Parameter(string name, string type, string usage, bool required = false, int dimensions = 0, bool visible = true)
        {
            var p = new XElement("Parameter", new XAttribute("Name", name), new XAttribute("TagType", "Base"), new XAttribute("DataType", type),
                dimensions == 0 ? null : new XAttribute("Dimensions", dimensions), new XAttribute("Usage", usage),
                new XAttribute("Required", required ? "true" : "false"), new XAttribute("Visible", visible ? "true" : "false"));
            if (usage == "InOut") p.Add(new XAttribute("Constant", "false"));
            else
            {
                p.Add(new XAttribute("Radix", "Decimal"), new XAttribute("ExternalAccess", "Read Only"));
                if (name != "EnableIn" && name != "EnableOut")
                    p.Add(new XElement("DefaultData", new XAttribute("Format", "Decorated"),
                        new XElement("DataValue", new XAttribute("DataType", type), new XAttribute("Radix", "Decimal"), new XAttribute("Value", "0"))));
            }
            return p;
        }
        var parameters = new XElement("Parameters", Parameter("EnableIn", "BOOL", "Input", visible: false), Parameter("EnableOut", "BOOL", "Output", visible: false));
        foreach (string name in new[] { "LastSent", "Current", "Received" }) parameters.Add(Parameter(name, "SINT", "InOut", true, 1));
        foreach (string name in new[] { "Error", "Status", "PlcChangedBytes", "ReceivedChangedBytes", "ConflictBytes", "AppliedBytes" })
            parameters.Add(Parameter(name, name == "Error" ? "BOOL" : "DINT", "Output"));
        return new XElement("AddOnInstructionDefinition", new XAttribute("Use", use), new XAttribute("Name", Name), new XAttribute("Revision", "1.0"),
            new XAttribute("Vendor", "votecoffee"), new XAttribute("ExecutePrescan", "false"), new XAttribute("ExecutePostscan", "false"), new XAttribute("ExecuteEnableInFalse", "false"),
            new XAttribute("CreatedDate", "2026-11-02T00:00:00.000Z"), new XAttribute("CreatedBy", "LogixTagRelay"),
            new XAttribute("EditedDate", "2026-11-02T00:00:00.000Z"), new XAttribute("EditedBy", "LogixTagRelay"), new XAttribute("SoftwareRevision", "v" + softwareRevision),
            new XElement("Description", new XCData("Three-way SINT merge; PLC wins. Actual InOut sizes are checked with SIZE. LastSent/Received must be distinct snapshots.")),
            parameters, new XElement("LocalTags", new[] { "LastSize", "CurrentSize", "ReceivedSize", "Index" }.Select(name =>
                new XElement("LocalTag", new XAttribute("Name", name), new XAttribute("DataType", "DINT"), new XAttribute("Radix", "Decimal"), new XAttribute("ExternalAccess", "None")))),
            new XElement("Routines", StRoutine("Logic", StructuredText)));
    }
    internal static XElement StRoutine(string name, string text, bool target = false) => new("Routine",
        target ? new XAttribute("Use", "Target") : null, new XAttribute("Name", name), new XAttribute("Type", "ST"),
        new XElement("STContent", text.ReplaceLineEndings("\n").TrimEnd('\n').Split('\n').Select((line, i) =>
            new XElement("Line", new XAttribute("Number", i), new XCData(line)))));
    internal static string Serialize(XDocument doc)
    {
        using var stream = new MemoryStream();
        using (var writer = XmlWriter.Create(stream, new XmlWriterSettings { Encoding = new UTF8Encoding(false), Indent = true, NewLineChars = "\n" })) doc.Save(writer);
        return Encoding.UTF8.GetString(stream.ToArray()) + "\n";
    }
}
