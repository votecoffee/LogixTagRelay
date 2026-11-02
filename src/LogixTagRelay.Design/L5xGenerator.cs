using System.Buffers.Binary;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace LogixTagRelay.Design;

internal static class L5xGenerator
{
    public static string Generate(SyncConfiguration config, string module, string revision)
    {
        string p = module + "_PS";
        var scopes = config.Fields.Select(f => Regex.Match(f.Path, @"^Program:([^.]+)\."))
            .Where(m => m.Success).Select(m => m.Groups[1].Value).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (scopes.Length > 1) throw new ArgumentException("Implicit groups may reference only one program scope.");
        string PathOf(FieldMap f) => Regex.Replace(f.Path, @"^Program:[^.]+\.", "");
        string rx = p + "_Rx", tx = p + "_Tx", current = p + "_Current", saved = p + "_LastSent", received = p + "_Received";
        int bytes = config.PlcToAppBytes, payload = bytes - ImplicitImage.HeaderBytes;
        int token = BinaryPrimitives.ReadInt32LittleEndian(Convert.FromHexString(config.SchemaHash).AsSpan(0, 4));
        var tags = new XElement("Tags");
        void Tag(string name, string type, int count = 0) => tags.Add(new XElement("Tag", new XAttribute("Name", name),
            new XAttribute("TagType", "Base"), new XAttribute("DataType", type), count == 0 ? null : new XAttribute("Dimensions", count),
            type == SintMergeAoi.Name ? null : new XAttribute("Radix", "Decimal"), new XAttribute("Constant", "false"), new XAttribute("ExternalAccess", "Read/Write")));
        Tag(rx, "SINT", bytes); Tag(tx, "SINT", bytes);
        foreach (string a in new[] { current, saved, received }) Tag(a, "SINT", payload);
        foreach (string s in new[] { "Magic", "Version", "Schema", "Valid", "CommandSequence", "BaselineSequence", "AcceptedSequence", "Sequence", "Bool", "HeaderMagic", "HeaderVersion", "HeaderSchema", "HeaderValid" }) Tag(p + "_" + s, "DINT");
        foreach (string s in new[] { "ApplyCommands", "Initialized", "Reset", "FrameValid", "CurrentValid", "MergedValid", "Publish" }) Tag(p + "_" + s, "BOOL");
        Tag(p + "_Watchdog", "TIMER"); Tag(p + "_Merge", SintMergeAoi.Name);
        var strings = config.Fields.Where(f => f.Type == PlcType.STRING).ToArray();
        for (int i = 0; i < strings.Length; i++) Tag(p + "_StringLen" + i, "DINT");
        var rungs = new List<(string Text, string Comment)>();
        void R(string text, string comment = "") => rungs.Add((text, comment));
        void Pack(string array, int subtract, string gate = "")
        {
            R($"{gate}FLL(0,{array}[0],{(subtract == 0 ? bytes : payload)});");
            foreach (var f in config.Fields)
            {
                string path = PathOf(f); int offset = f.Offset - subtract;
                if (f.Type == PlcType.BOOL)
                {
                    R($"{gate}MOV(0,{p}_Bool);"); R($"{gate}XIC({path})MOV(1,{p}_Bool);");
                    R($"{gate}CPS({p}_Bool,{array}[{offset}],4);");
                }
                else R($"{gate}CPS({path},{array}[{offset}],{(f.Type == PlcType.STRING ? (f.WireBytes + 3) / 4 * 4 : f.WireBytes)});");
            }
        }
        R($"XIC({p}_Reset)OTU({p}_Initialized)MOV(0,{p}_Sequence)MOV(0,{p}_AcceptedSequence);", "Hold Reset during connection/state reconciliation; release it before enabling commands. Restart the API session after reset.");
        R($"CPS({module}:I.Data[0],{rx}[0],{bytes});", "Snapshot module input TO the PLC. Header is excluded from the three merge arrays.");
        string[] inputHeaders = ["Magic", "Version", "Schema", "CommandSequence", "BaselineSequence", "Valid"];
        for (int i = 0; i < 6; i++) R($"CPS({rx}[{i * 4}],{p}_{inputHeaders[i]},1);");
        Pack(current, ImplicitImage.HeaderBytes);
        string currentValid = $"XIO({p}_Reset)";
        foreach (var f in strings) currentValid += $"GEQ({PathOf(f)}.LEN,0)LEQ({PathOf(f)}.LEN,{f.StringCapacity})";
        R($"{currentValid}OTE({p}_CurrentValid);");
        string frameValid = $"XIC({p}_ApplyCommands)XIC({p}_Initialized)XIC({p}_CurrentValid)EQU({p}_Magic,{ImplicitImage.Magic})EQU({p}_Version,{ImplicitImage.ProtocolVersion})EQU({p}_Schema,{token})EQU({p}_Valid,1)GRT({p}_CommandSequence,0)NEQ({p}_CommandSequence,{p}_AcceptedSequence)EQU({p}_BaselineSequence,{p}_Sequence)";
        for (int i = 0; i < strings.Length; i++)
        {
            R($"CPS({rx}[{strings[i].Offset}],{p}_StringLen{i},1);");
            frameValid += $"GEQ({p}_StringLen{i},0)LEQ({p}_StringLen{i},{strings[i].StringCapacity})";
        }
        R($"{frameValid}OTE({p}_FrameValid);", "Accept only a fresh command referencing the exact frozen snapshot last written to module output. Validate incoming string lengths before merge.");
        R($"XIC({p}_FrameValid)RES({p}_Watchdog);");
        R($"TON({p}_Watchdog,1000,0);", "Repeated wire packets cannot refresh this watchdog. Fresh matching generations can recover it.");
        R($"XIC({p}_FrameValid)CPS({rx}[24],{received}[0],{payload});");
        foreach (var f in config.Fields.Where(f => f.Direction == Direction.PlcToApp))
            R($"XIC({p}_FrameValid)CPS({current}[{f.Offset - 24}],{received}[{f.Offset - 24}],{(f.WireBytes + 3) / 4 * 4});", "Preserve PLC-only ownership in the wrapper; the AOI has no type or direction metadata.");
        R($"XIC({p}_FrameValid){SintMergeAoi.Name}({p}_Merge,{saved},{current},{received});", "The Structured Text AOI merges exactly three equally sized SINT payload arrays. PLC wins each changed byte.");
        string mergedValid = $"XIC({p}_FrameValid)XIO({p}_Merge.Error)";
        for (int i = 0; i < strings.Length; i++)
        {
            R($"CPS({current}[{strings[i].Offset - 24}],{p}_StringLen{i},1);");
            mergedValid += $"GEQ({p}_StringLen{i},0)LEQ({p}_StringLen{i},{strings[i].StringCapacity})";
        }
        R($"{mergedValid}OTE({p}_MergedValid);", "Validate merged STRING LENs before changing any destination. Invalid merges are not applied or acknowledged.");
        foreach (var f in config.Fields.Where(f => f.Direction == Direction.AppToPlc))
        {
            string path = PathOf(f), gate = $"XIC({p}_MergedValid)"; int offset = f.Offset - 24;
            if (f.Type == PlcType.BOOL)
            {
                R($"{gate}CPS({current}[{offset}],{p}_Bool,1);");
                R($"{gate}[NEQ({p}_Bool,0)OTL({path}),EQU({p}_Bool,0)OTU({path})];");
            }
            else R($"{gate}CPS({current}[{offset}],{path},1);");
        }
        R($"XIC({p}_MergedValid)MOV({p}_CommandSequence,{p}_AcceptedSequence);", "ACK means merge completed, including bytes retained by the PLC. It does not mean every requested API byte won.");
        R($"XIC({p}_CurrentValid)[XIO({p}_Initialized),XIC({p}_MergedValid)]OTE({p}_Publish);");
        string publish = $"XIC({p}_Publish)";
        Pack(tx, 0, publish);
        R($"{publish}EQU({p}_Sequence,2147483647)MOV(0,{p}_Sequence);");
        R($"{publish}ADD({p}_Sequence,1,{p}_Sequence);", "Advance only after startup or a completed exchange; hold the snapshot while waiting for its reply.");
        R($"{publish}MOV({ImplicitImage.Magic},{p}_HeaderMagic)MOV({ImplicitImage.ProtocolVersion},{p}_HeaderVersion)MOV({token},{p}_HeaderSchema)MOV(1,{p}_HeaderValid);");
        string[] headers = ["HeaderMagic", "HeaderVersion", "HeaderSchema", "Sequence", "AcceptedSequence", "HeaderValid"];
        for (int i = 0; i < headers.Length; i++) R($"{publish}CPS({p}_{headers[i]},{tx}[{i * 4}],4);");
        R($"{publish}CPS({tx}[0],{module}:O.Data[0],{bytes});", "Write output FROM the PLC, then save exactly that payload as the baseline.");
        R($"{publish}CPS({tx}[24],{saved}[0],{payload});");
        R($"{publish}OTL({p}_Initialized);");
        var routine = new XElement("Routine", new XAttribute("Use", "Target"), new XAttribute("Name", p + "_Sync"), new XAttribute("Type", "RLL"),
            new XElement("RLLContent", rungs.Select((r, i) => new XElement("Rung", new XAttribute("Number", i), new XAttribute("Type", "N"),
                string.IsNullOrEmpty(r.Comment) ? null : new XElement("Comment", new XCData(r.Comment)), new XElement("Text", new XCData(r.Text))))));
        return SintMergeAoi.Serialize(new XDocument(new XDeclaration("1.0", "UTF-8", "yes"), new XElement("RSLogix5000Content",
            new XAttribute("SchemaRevision", "1.0"), new XAttribute("SoftwareRevision", revision), new XAttribute("TargetName", p + "_Sync"), new XAttribute("TargetType", "Routine"),
            new XAttribute("ContainsContext", "true"), new XAttribute("ExportOptions", "References NoRawData DecoratedData Context Dependencies"),
            new XElement("Controller", new XAttribute("Use", "Context"), new XAttribute("Name", "LogixRelayContext"), new XElement("DataTypes"), new XElement("Modules"),
                new XElement("AddOnInstructionDefinitions", new XAttribute("Use", "Context"), SintMergeAoi.Definition(revision)), new XElement("Tags"),
                new XElement("Programs", new XElement("Program", new XAttribute("Use", "Context"), new XAttribute("Name", scopes.FirstOrDefault() ?? "MainProgram"), tags, new XElement("Routines", routine)))))));
    }
}
