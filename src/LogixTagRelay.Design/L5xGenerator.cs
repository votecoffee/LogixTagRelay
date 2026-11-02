using System.Buffers.Binary;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace LogixTagRelay.Design;

internal static class L5xGenerator
{
    public static string Generate(SyncConfiguration config, string module, string revision)
    {
        // No native UDT offsets: numeric fields copy directly at explicit wire offsets.
        // BOOL is converted through a DINT temporary. CPS lengths count DESTINATION elements.
        string prefix = module + "_PS";
        var programScopes = config.Fields.Select(f => Regex.Match(f.Path, @"^Program:([^.]+)\."))
            .Where(m => m.Success).Select(m => m.Groups[1].Value).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (programScopes.Length > 1) throw new ArgumentException("Implicit groups may reference only one program scope.");
        string LocalPath(string path) => Regex.Replace(path, @"^Program:[^.]+\.", "");
        string rx = prefix + "_Rx", tx = prefix + "_Tx";
        int token = BinaryPrimitives.ReadInt32LittleEndian(Convert.FromHexString(config.SchemaHash).AsSpan(0, 4));
        var tags = new XElement("Tags");
        void Tag(string name, string type, int count = 0) => tags.Add(new XElement("Tag", new XAttribute("Name", name),
            new XAttribute("TagType", "Base"), new XAttribute("DataType", type),
            count == 0 ? null : new XAttribute("Dimensions", count), new XAttribute("Radix", "Decimal"),
            new XAttribute("Constant", "false"), new XAttribute("ExternalAccess", "Read/Write")));
        Tag(rx, "SINT", config.AppToPlcBytes); Tag(tx, "SINT", config.PlcToAppBytes);
        foreach (string suffix in new[] { "Magic", "Version", "Schema", "Valid", "CommandSequence", "AcceptedSequence", "Sequence", "Bool", "HeaderMagic", "HeaderVersion", "HeaderSchema", "HeaderValid" })
            Tag(prefix + "_" + suffix, "DINT");
        Tag(prefix + "_ApplyCommands", "BOOL"); // Integrator must drive this permission from connection/watchdog policy.
        Tag(prefix + "_Watchdog", "TIMER");
        Tag(prefix + "_FrameValid", "BOOL");
        var commandStrings = config.Fields.Where(f => f.Direction == Direction.AppToPlc && f.Type == PlcType.STRING).ToArray();
        for (int i = 0; i < commandStrings.Length; i++) Tag(prefix + "_StringLen" + i, "DINT");
        var rungs = new List<(string Text, string Comment)>();
        void Rung(string text, string comment = "") => rungs.Add((text, comment));
        Rung($"CPS({module}:I.Data[0],{rx}[0],{config.AppToPlcBytes});", "Snapshot PC-to-PLC assembly once before decoding. Module I is input TO the PLC.");
        for (int i = 0; i < 6; i++)
        {
            string suffix = new[] { "Magic", "Version", "Schema", "CommandSequence", "AcceptedSequence", "Valid" }[i];
            // Do not read peer acknowledgement into local AcceptedSequence.
            if (i == 4) continue;
            Rung($"CPS({rx}[{i * 4}],{prefix}_{suffix},1);");
        }
        string headerValid = $"XIC({prefix}_ApplyCommands)EQU({prefix}_Magic,{ImplicitImage.Magic})EQU({prefix}_Version,{ImplicitImage.ProtocolVersion})EQU({prefix}_Schema,{token})EQU({prefix}_Valid,1)";
        for (int i = 0; i < commandStrings.Length; i++)
        {
            Rung($"CPS({rx}[{commandStrings[i].Offset}],{prefix}_StringLen{i},1);");
            headerValid += $"GEQ({prefix}_StringLen{i},0)LEQ({prefix}_StringLen{i},{commandStrings[i].StringCapacity})";
        }
        Rung($"{headerValid}OTE({prefix}_FrameValid);", "Validate every command STRING LEN before any application destination is changed.");
        Rung($"XIC({prefix}_FrameValid)NEQ({prefix}_CommandSequence,{prefix}_AcceptedSequence)RES({prefix}_Watchdog);");
        Rung($"TON({prefix}_Watchdog,1000,0);", "A managed application stall cannot refresh this watchdog by retransmitting the same sequence.");
        string valid = $"XIC({prefix}_FrameValid)XIO({prefix}_Watchdog.DN)NEQ({prefix}_CommandSequence,{prefix}_AcceptedSequence)";
        foreach (var field in config.Fields.Where(f => f.Direction == Direction.AppToPlc))
        {
            string path = LocalPath(field.Path);
            if (field.Type == PlcType.BOOL)
            {
                Rung($"{valid}CPS({rx}[{field.Offset}],{prefix}_Bool,1);");
                // Gate the OTE in a branch so an invalid frame does not clear a prior command.
                Rung($"{valid}[NEQ({prefix}_Bool,0)OTL({path}),EQU({prefix}_Bool,0)OTU({path})];");
            }
            else Rung($"{valid}CPS({rx}[{field.Offset}],{path},1);", field.Type == PlcType.STRING ?
                "One native counted string: LEN + declared SINT DATA + DINT alignment; capacity validated before copying." : "");
        }
        Rung($"{valid}MOV({prefix}_CommandSequence,{prefix}_AcceptedSequence);", "Acknowledgement follows all command copies; application logic should consume staging values deliberately.");
        Rung($"FLL(0,{tx}[0],{config.PlcToAppBytes});");
        Rung($"MOV(1,{prefix}_HeaderValid);");
        foreach (var field in config.Fields)
        {
            string path = LocalPath(field.Path);
            if (field.Type == PlcType.BOOL)
            {
                Rung($"MOV(0,{prefix}_Bool);"); Rung($"XIC({path})MOV(1,{prefix}_Bool);");
                Rung($"CPS({prefix}_Bool,{tx}[{field.Offset}],4);");
            }
            else Rung($"CPS({path},{tx}[{field.Offset}],{(field.Type == PlcType.STRING ? (field.WireBytes + 3) / 4 * 4 : field.WireBytes)});");
            if (field.Type == PlcType.STRING)
            {
                Rung($"LES({path}.LEN,0)MOV(0,{prefix}_HeaderValid);");
                Rung($"GRT({path}.LEN,{field.StringCapacity})MOV(0,{prefix}_HeaderValid);");
            }
        }
        Rung($"EQU({prefix}_Sequence,2147483647)MOV(-1,{prefix}_Sequence);");
        Rung($"ADD({prefix}_Sequence,1,{prefix}_Sequence);", "Wrap to zero before signed ADD overflow. Sequence advances on each routine execution.");
        Rung($"MOV({ImplicitImage.Magic},{prefix}_HeaderMagic)MOV({ImplicitImage.ProtocolVersion},{prefix}_HeaderVersion)MOV({token},{prefix}_HeaderSchema);");
        var headers = new[] { "HeaderMagic", "HeaderVersion", "HeaderSchema", "Sequence", "AcceptedSequence", "HeaderValid" };
        for (int i = 0; i < headers.Length; i++) Rung($"CPS({prefix}_{headers[i]},{tx}[{i * 4}],4);");
        Rung($"CPS({tx}[0],{module}:O.Data[0],{config.PlcToAppBytes});", "Module O is output FROM the PLC. Only one routine/task should own these bytes.");
        var routine = new XElement("Routine", new XAttribute("Use", "Target"), new XAttribute("Name", prefix + "_Sync"),
            new XAttribute("Type", "RLL"), new XElement("RLLContent", rungs.Select((r, i) =>
                new XElement("Rung", new XAttribute("Number", i), new XAttribute("Type", "N"),
                    string.IsNullOrEmpty(r.Comment) ? null : new XElement("Comment", new XCData(r.Comment)),
                    new XElement("Text", new XCData(r.Text))))));
        var doc = new XDocument(new XDeclaration("1.0", "UTF-8", "yes"), new XElement("RSLogix5000Content",
            new XAttribute("SchemaRevision", "1.0"), new XAttribute("SoftwareRevision", revision),
            new XAttribute("TargetName", prefix + "_Sync"), new XAttribute("TargetType", "Routine"),
            new XAttribute("ContainsContext", "true"), new XAttribute("ExportOptions", "References NoRawData DecoratedData Context Dependencies"),
            new XElement("Controller", new XAttribute("Use", "Context"), new XAttribute("Name", "LogixRelayContext"),
                new XElement("DataTypes"), new XElement("Modules"), new XElement("AddOnInstructionDefinitions"), new XElement("Tags"),
                new XElement("Programs", new XElement("Program", new XAttribute("Use", "Context"),
                    new XAttribute("Name", programScopes.FirstOrDefault() ?? "MainProgram"), tags, new XElement("Routines", routine))))));
        using var stream = new MemoryStream();
        using (var writer = XmlWriter.Create(stream, new XmlWriterSettings { Encoding = new UTF8Encoding(false), Indent = true, NewLineChars = "\n" })) doc.Save(writer);
        return Encoding.UTF8.GetString(stream.ToArray()) + "\n";
    }
}
