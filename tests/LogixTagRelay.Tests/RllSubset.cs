using System.Buffers.Binary;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using LogixTagRelay;
using LogixTagRelay.Design;

// Strict offline executor for the emitted wrapper instructions, not a Logix compiler.
sealed class RllSubset
{
    private sealed record Cell(byte[] Data, int ElementBytes);
    private readonly Dictionary<string, Cell> cells = new(StringComparer.OrdinalIgnoreCase);
    private readonly string[] rungs;
    private readonly StSubset merge;
    public RllSubset(GeneratedBundle bundle, string module = "LogixRelay")
    {
        var doc = XDocument.Parse(bundle.RoutineL5x);
        rungs = doc.Descendants("Rung").Select(r => r.Element("Text")!.Value).ToArray();
        merge = new(string.Join("\n", doc.Descendants("AddOnInstructionDefinition").Single().Descendants("Line").Select(l => l.Value)));
        foreach (var t in doc.Descendants("Tag"))
        {
            string name = t.Attribute("Name")!.Value, type = t.Attribute("DataType")!.Value;
            int count = (int?)t.Attribute("Dimensions") ?? 1;
            if (type == SintMergeAoi.Name) { Add(name + ".Error", 4); continue; }
            if (type == "TIMER") { Add(name + ".DN", 4); continue; }
            int size = type == "SINT" ? 1 : 4; Add(name, size, count);
        }
        Add(module + ":I.Data", 1, bundle.Configuration.AppToPlcBytes);
        Add(module + ":O.Data", 1, bundle.Configuration.PlcToAppBytes);
        foreach (var f in bundle.Configuration.Fields)
        {
            string path = Regex.Replace(f.Path, @"^Program:[^.]+\.", "");
            Add(path, (f.WireBytes + 3) / 4 * 4);
            if (f.Type == PlcType.STRING) cells.Add(path + ".LEN", cells[path] with { ElementBytes = 4 });
        }
    }
    private void Add(string name, int size, int count = 1) => cells.Add(name, new(new byte[size * count], size));
    private (Cell Cell, int Offset) Address(string name)
    {
        if (cells.TryGetValue(name, out var cell)) return (cell, 0);
        var m = Regex.Match(name, @"^(.*)\[(\d+)\]$");
        if (!m.Success || !cells.TryGetValue(m.Groups[1].Value, out cell)) throw new FormatException("Unknown model address " + name);
        return (cell, int.Parse(m.Groups[2].Value) * cell.ElementBytes);
    }
    public byte[] Bytes(string name) { var (c, o) = Address(name); return c.Data[o..]; }
    public void Bytes(string name, byte[] bytes) { var (c, o) = Address(name); bytes.CopyTo(c.Data, o); }
    public int Get(string name) => int.TryParse(name, out int n) ? n : BinaryPrimitives.ReadInt32LittleEndian(Bytes(name));
    public void Set(string name, int value) { var (c, o) = Address(name); BinaryPrimitives.WriteInt32LittleEndian(c.Data.AsSpan(o), value); }
    public void Scan() { foreach (var text in rungs) { int at = 0; Evaluate(text, ref at, true); if (at != text.Length) throw new FormatException("Unparsed rung"); } }
    private bool Evaluate(string text, ref int at, bool power)
    {
        while (at < text.Length && text[at] != ',' && text[at] != ']')
        {
            if (text[at] == ';') { at++; continue; }
            if (text[at] == '[')
            {
                at++; bool any = false;
                do { any |= Evaluate(text, ref at, power); if (text[at] != ',') break; at++; } while (true);
                if (text[at++] != ']') throw new FormatException("Unclosed branch");
                power = any; continue;
            }
            int open = text.IndexOf('(', at), close = text.IndexOf(')', open);
            if (open < 0 || close < 0) throw new FormatException("Unsupported rung");
            string op = text[at..open]; string[] a = text[(open + 1)..close].Split(','); at = close + 1;
            // Commas inside Logix multidimensional subscripts are not argument separators.
            if (a.Any(x => x.Contains('[') && !x.Contains(']')))
                a = Regex.Split(text[(open + 1)..close], @",(?![^\[]*\])");
            switch (op)
            {
                case "XIC": power &= Get(a[0]) != 0; break;
                case "XIO": power &= Get(a[0]) == 0; break;
                case "EQU": power &= Get(a[0]) == Get(a[1]); break;
                case "NEQ": power &= Get(a[0]) != Get(a[1]); break;
                case "GEQ": power &= Get(a[0]) >= Get(a[1]); break;
                case "LEQ": power &= Get(a[0]) <= Get(a[1]); break;
                case "GRT": power &= Get(a[0]) > Get(a[1]); break;
                case "OTE": Set(a[0], power ? 1 : 0); break;
                case "OTL": if (power) Set(a[0], 1); break;
                case "OTU": if (power) Set(a[0], 0); break;
                case "MOV": if (power) Set(a[1], Get(a[0])); break;
                case "ADD": if (power) Set(a[2], checked(Get(a[0]) + Get(a[1]))); break;
                case "FLL": if (power) { if (Get(a[0]) != 0) throw new FormatException("Nonzero FLL unsupported"); var (d,o) = Address(a[1]); d.Data.AsSpan(o, Get(a[2]) * d.ElementBytes).Clear(); } break;
                case "CPS": if (power) { var (s,so) = Address(a[0]); var (d,dp) = Address(a[1]); int n = Get(a[2]) * d.ElementBytes; s.Data.AsSpan(so,n).ToArray().CopyTo(d.Data,dp); } break;
                case "RES": if (power) Set(a[0] + ".DN", 0); break;
                case "TON": break; // Timer elapsed time is outside this model; native timing acceptance remains required.
                case SintMergeAoi.Name:
                    if (power)
                    {
                        var b = Bytes(a[2]).Select(x => unchecked((sbyte)x)).ToArray();
                        var result = merge.Run(Bytes(a[1]).Select(x => unchecked((sbyte)x)).ToArray(), b, Bytes(a[3]).Select(x => unchecked((sbyte)x)).ToArray());
                        Bytes(a[2], b.Select(x => unchecked((byte)x)).ToArray()); Set(a[0] + ".Error", (int)result.Get("Error"));
                    }
                    break;
                default: throw new FormatException("Unsupported wrapper instruction " + op);
            }
        }
        return power;
    }
}
