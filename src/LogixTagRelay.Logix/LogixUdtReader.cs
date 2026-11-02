using System.Buffers.Binary;
using System.Text;
using System.Text.RegularExpressions;

namespace LogixTagRelay.Logix;

/// <summary>Reads UDT snapshots using controller-reported native offsets; never writes a UDT buffer.</summary>
public sealed class LogixUdtReader(Func<string, CancellationToken, ValueTask<byte[]>> readRaw)
{
    private sealed record Member(string Name, ushort Type, ushort Metadata, int Offset);
    private sealed record Template(string Name, int Size, List<Member> Members);
    private sealed record Symbol(ushort Type, int[] Dimensions);
    private sealed record Binding(FieldMap Field, int Offset, int Bit = -1);
    private sealed record Plan(Template Template, List<Binding> Bindings);
    private readonly Dictionary<string, Dictionary<string, Symbol>> symbols = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<int, Template> templates = [];
    private readonly Dictionary<string, Plan> plans = [];
    public async ValueTask<IReadOnlyDictionary<string, object>> ReadAsync(ExplicitReadGroup group, IReadOnlyList<FieldMap> fields, CancellationToken ct = default)
    {
        ExplicitReads.Validate(fields, [group]);
        var mappings = fields.ToDictionary(f => f.Path, StringComparer.OrdinalIgnoreCase);
        string key = group.Path + "|" + group.DataType + "|" + string.Join(";", group.Fields.Select(p =>
        {
            var f = mappings[p]; return $"{p}:{f.Type}:{f.StringCapacity}";
        }));
        if (!plans.TryGetValue(key, out var plan))
        {
            var template = await RootAsync(group, ct).ConfigureAwait(false);
            var bindings = new List<Binding>();
            foreach (string path in group.Fields)
            {
                var field = mappings[path];
                bindings.Add(await BindAsync(template, field, path[(group.Path.Length + 1)..], ct).ConfigureAwait(false));
            }
            plan = new(template, bindings); plans.Add(key, plan);
        }
        byte[] image = await readRaw(group.Path, ct).ConfigureAwait(false);
        if (image.Length != plan.Template.Size) throw new FormatException($"UDT {group.Path} size changed or differs from its template. Refresh the contract and recreate the transport.");
        var result = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        foreach (var b in plan.Bindings)
            result.Add(b.Field.Path, b.Bit >= 0 ? (object)((image[b.Offset] & (1 << b.Bit)) != 0) : PlcTypes.Decode(b.Field, image.AsSpan(b.Offset)));
        return result;
    }
    private async Task<Template> RootAsync(ExplicitReadGroup group, CancellationToken ct)
    {
        string path = group.Path, scope = "";
        if (path.StartsWith("Program:", StringComparison.OrdinalIgnoreCase))
        {
            int dot = path.IndexOf('.'); scope = path[..(dot + 1)]; path = path[(dot + 1)..];
        }
        var parts = path.Split('.'); var root = Part(parts[0]);
        if (!symbols.TryGetValue(scope, out var listing))
        {
            byte[] bytes = await readRaw(scope + "@tags", ct).ConfigureAwait(false);
            listing = new(StringComparer.OrdinalIgnoreCase);
            for (int at = 0; at < bytes.Length;)
            {
                if (bytes.Length - at < 22) throw new FormatException("Truncated symbol descriptor.");
                int length = U16(bytes, at + 20);
                if (length < 1 || at + 22L + length > bytes.Length) throw new FormatException("Invalid symbol name length.");
                int[] dimensions = new[] { I32(bytes, at + 8), I32(bytes, at + 12), I32(bytes, at + 16) }.Reverse().SkipWhile(n => n == 0).Reverse().ToArray();
                listing.Add(Encoding.ASCII.GetString(bytes, at + 22, length), new(U16(bytes, at + 4), dimensions));
                at += 22 + length;
                if (listing.Count > 20000) throw new FormatException("Symbol count exceeds limit.");
            }
            symbols.Add(scope, listing);
        }
        if (!listing.TryGetValue(root.Name, out var symbol)) throw new FormatException($"PLC symbol not found: {scope}{root.Name}");
        CheckIndices(root.Indices, symbol.Dimensions);
        var template = await StructAsync(symbol.Type, ct).ConfigureAwait(false);
        foreach (string component in parts.Skip(1))
        {
            var part = Part(component); var member = Find(template, part.Name);
            CheckIndices(part.Indices, Dimensions(member));
            template = await StructAsync(member.Type, ct).ConfigureAwait(false);
        }
        if (!template.Name.Equals(group.DataType, StringComparison.OrdinalIgnoreCase)) throw new FormatException($"PLC UDT type {template.Name} differs from expected {group.DataType}.");
        return template;
    }
    private async Task<Binding> BindAsync(Template root, FieldMap field, string relative, CancellationToken ct)
    {
        var parts = relative.Split('.'); if (parts.Length > 32) throw new FormatException("UDT nesting limit exceeded.");
        var current = root; int offset = 0;
        for (int i = 0; i < parts.Length; i++)
        {
            var part = Part(parts[i]); var member = Find(current, part.Name);
            CheckIndices(part.Indices, Dimensions(member));
            bool structure = (member.Type & 0x8000) != 0;
            Template? nested = structure ? await StructAsync(member.Type, ct).ConfigureAwait(false) : null;
            int scalar = member.Type & 0x0FFF;
            int nativeSize = nested?.Size ?? scalar switch { 0xC1 or 0xC2 => 1, 0xC3 => 2, 0xC4 or 0xCA => 4, 0xC5 => 8, _ => throw new FormatException("Unsupported native member type.") };
            if (scalar == 0xC1 && part.Indices.Length > 0) throw new FormatException("Read BOOL array members with individual bit addresses, outside the UDT read group.");
            int local = checked(member.Offset + (part.Indices.Length == 0 ? 0 : part.Indices[0] * nativeSize));
            if (local < 0 || (long)local + nativeSize > current.Size) throw new FormatException("Member exceeds its reported native UDT size.");
            offset = checked(offset + local);
            if (i < parts.Length - 1) { current = nested ?? throw new FormatException("Expected a nested native UDT."); continue; }
            if (field.Type == PlcType.STRING)
            {
                if (nested is null) throw new FormatException("Expected native STRING structure.");
                var visible = nested.Members.Where(m => !m.Name.StartsWith("ZZZZ", StringComparison.Ordinal)).ToArray();
                var len = visible.SingleOrDefault(m => m.Name == "LEN"); var data = visible.SingleOrDefault(m => m.Name == "DATA");
                if (visible.Length != 2 || len is null || data is null || len.Type != 0xC4 || len.Offset != 0 || data.Type != 0x20C2 || data.Offset != 4 ||
                    data.Metadata != field.StringCapacity || nested.Size != (field.WireBytes + 3) / 4 * 4) throw new FormatException("Native STRING layout/capacity differs from the contract.");
                return new(field, offset);
            }
            int expected = field.Type switch { PlcType.BOOL => 0xC1, PlcType.SINT => 0xC2, PlcType.INT => 0xC3, PlcType.DINT => 0xC4, PlcType.LINT => 0xC5, PlcType.REAL => 0xCA, _ => -1 };
            if (structure || scalar != expected) throw new FormatException($"Native type differs for {field.Path}.");
            if (field.Type == PlcType.BOOL)
            {
                if (member.Metadata > 7) throw new FormatException("BOOL bit index is outside its native hidden byte.");
                return new(field, offset, member.Metadata);
            }
            return new(field, offset);
        }
        throw new FormatException("Empty UDT member path.");
    }
    private async Task<Template> StructAsync(ushort type, CancellationToken ct)
    {
        if ((type & 0x8000) == 0) throw new FormatException("Expected a native UDT type.");
        int id = type & 0x0FFF;
        if (templates.TryGetValue(id, out var cached)) return cached;
        byte[] bytes = await readRaw($"@udt/{id}", ct).ConfigureAwait(false);
        if (bytes.Length < 14 || U16(bytes, 0) != id) throw new FormatException("Invalid UDT template identity/header.");
        int size = I32(bytes, 6), count = U16(bytes, 10), names = checked(14 + count * 8);
        if (size < 1 || size > 16 * 1024 * 1024 || count < 1 || count > 20000 || names > bytes.Length) throw new FormatException("Invalid UDT size/member count.");
        string name = CString(bytes, ref names).Split(';')[0];
        var members = new List<Member>();
        for (int i = 0; i < count; i++) members.Add(new(CString(bytes, ref names), U16(bytes, 16 + i * 8), U16(bytes, 14 + i * 8), I32(bytes, 18 + i * 8)));
        if (members.Select(m => m.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != count) throw new FormatException("Duplicate native UDT member names.");
        var result = new Template(name, size, members); templates.Add(id, result); return result;
    }
    private static Member Find(Template template, string name) => template.Members.SingleOrDefault(m => m.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) ?? throw new FormatException($"Native member not found: {template.Name}.{name}");
    private static int[] Dimensions(Member member) => ((member.Type >> 13) & 3) switch { 0 => [], 1 when member.Metadata > 0 => [member.Metadata], _ => throw new FormatException("Native UDT member arrays must be one dimensional.") };
    private static void CheckIndices(int[] indices, int[] dimensions)
    {
        if (indices.Length != dimensions.Length || indices.Where((n, i) => n < 0 || n >= dimensions[i]).Any()) throw new FormatException("Native array coordinates differ from the contract.");
    }
    private static (string Name, int[] Indices) Part(string path)
    {
        var m = Regex.Match(path, @"^([A-Za-z_][A-Za-z0-9_]*)(?:\[(\d+(?:,\d+){0,2})\])?$");
        if (!m.Success) throw new FormatException("Invalid native member path.");
        return (m.Groups[1].Value, m.Groups[2].Success ? m.Groups[2].Value.Split(',').Select(int.Parse).ToArray() : []);
    }
    private static ushort U16(byte[] bytes, int at) => BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(at));
    private static int I32(byte[] bytes, int at) => checked((int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(at)));
    private static string CString(byte[] bytes, ref int at)
    {
        int end = Array.IndexOf(bytes, (byte)0, at); if (end < 0) throw new FormatException("Unterminated UDT name.");
        string value = Encoding.ASCII.GetString(bytes, at, end - at); at = end + 1; return value;
    }
}
