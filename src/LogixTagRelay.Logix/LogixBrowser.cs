using System.Buffers.Binary;
using System.Text;
using LogixTagRelay.Design;

namespace LogixTagRelay.Logix;

/// <summary>Read-only online symbol/template discovery. Permissions are unknown until matched to an export.</summary>
public sealed class LogixBrowser(string gateway, string route = "1,0", int timeoutMs = 5000)
{
    private sealed record Symbol(string Name, ushort Type, uint[] Dimensions);
    private sealed record Member(string Name, ushort Type, ushort Metadata);
    private sealed record Template(string Name, List<Member> Members);
    private readonly Dictionary<int, Template> templates = [];
    private int expanded;
    public async Task<TagCatalog> BrowseAsync(CancellationToken cancellationToken = default)
    {
        templates.Clear(); expanded = 0;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(60));
        var roots = await SymbolsAsync("@tags", timeout.Token).ConfigureAwait(false);
        var tags = new List<TagNode>();
        foreach (var symbol in roots)
        {
            if (symbol.Name.StartsWith("Program:", StringComparison.Ordinal))
            {
                var programTags = await SymbolsAsync(symbol.Name + ".@tags", timeout.Token).ConfigureAwait(false);
                foreach (var tag in programTags)
                    tags.Add(await BuildAsync(symbol.Name[8..] + "_" + tag.Name, symbol.Name + "." + tag.Name,
                        tag.Type, tag.Dimensions, 0, timeout.Token).ConfigureAwait(false));
            }
            else tags.Add(await BuildAsync(symbol.Name, symbol.Name, symbol.Type, symbol.Dimensions, 0, timeout.Token).ConfigureAwait(false));
        }
        return new("Online:" + gateway, "unknown", tags);
    }
    private async Task<byte[]> ReadRawAsync(string name, CancellationToken cancellationToken)
    {
        using var transport = new NativeTagOwner(gateway, route, timeoutMs, name);
        await transport.Tag.ReadAsync(cancellationToken).ConfigureAwait(false);
        return transport.Tag.GetBuffer();
    }
    private sealed class NativeTagOwner : IDisposable
    {
        public libplctag.Tag Tag { get; }
        public NativeTagOwner(string host, string path, int timeout, string name) => Tag = new()
        {
            Gateway = host, Path = path, Name = name, Protocol = libplctag.Protocol.ab_eip,
            PlcType = libplctag.PlcType.ControlLogix, Timeout = TimeSpan.FromMilliseconds(timeout)
        };
        public void Dispose() => Tag.Dispose();
    }
    private async Task<List<Symbol>> SymbolsAsync(string name, CancellationToken cancellationToken)
    {
        byte[] bytes = await ReadRawAsync(name, cancellationToken).ConfigureAwait(false);
        var result = new List<Symbol>();
        for (int offset = 0; offset < bytes.Length;)
        {
            if (bytes.Length - offset < 22) throw new FormatException("Truncated libplctag symbol record.");
            int nameLength = U16(bytes, offset + 20);
            if (nameLength == 0 || offset + 22L + nameLength > bytes.Length) throw new FormatException("Invalid symbol name length.");
            result.Add(new(Encoding.ASCII.GetString(bytes, offset + 22, nameLength), U16(bytes, offset + 4),
                [U32(bytes, offset + 8), U32(bytes, offset + 12), U32(bytes, offset + 16)]));
            offset += 22 + nameLength;
            if (result.Count > 20000) throw new FormatException("Symbol limit exceeded.");
        }
        return result;
    }
    private async Task<Template> TemplateAsync(int id, CancellationToken cancellationToken)
    {
        if (templates.TryGetValue(id, out var cached)) return cached;
        byte[] bytes = await ReadRawAsync($"@udt/{id}", cancellationToken).ConfigureAwait(false);
        if (bytes.Length < 14) throw new FormatException("Truncated UDT header.");
        int count = U16(bytes, 10), namesOffset = checked(14 + count * 8);
        if (namesOffset > bytes.Length) throw new FormatException("Truncated UDT descriptors.");
        string templateName = ReadCString(bytes, ref namesOffset).Split(';')[0];
        var members = new List<Member>();
        for (int i = 0; i < count; i++)
            members.Add(new(ReadCString(bytes, ref namesOffset), U16(bytes, 14 + i * 8 + 2), U16(bytes, 14 + i * 8)));
        var result = new Template(templateName, members); templates.Add(id, result); return result;
    }
    private async Task<TagNode> BuildAsync(string name, string path, ushort type, uint[] dimensions, int depth, CancellationToken cancellationToken)
    {
        if (++expanded > 20000 || depth > 32) throw new FormatException("Online expansion limit exceeded.");
        var dims = dimensions.ToList();
        while (dims.Count > 0 && dims[^1] == 0) dims.RemoveAt(dims.Count - 1);
        if (dims.Count > 0)
        {
            if (dims.Count > 3 || dims.Any(d => d == 0 || d > 20000))
                return new(name, path, "Array", "Unknown", [], true, "Unsupported array dimensions.");
            int[] shape = dims.Select(d => checked((int)d)).ToArray();
            var elements = new List<TagNode>();
            for (int i = 0; i < ArrayShape.Count(shape); i++) elements.Add(await BuildAsync($"[{ArrayShape.Subscript(i, shape)}]", $"{path}[{ArrayShape.Subscript(i, shape)}]", (ushort)(type & ~0x6000), [], depth + 1, cancellationToken).ConfigureAwait(false));
            return new(name, path, elements[0].DataType, "Unknown", elements, true, Dimensions: shape);
        }
        string? atomic = (type & 0xFFF) switch { 0xC1 => "BOOL", 0xC2 => "SINT", 0xC3 => "INT", 0xC4 => "DINT", 0xC5 => "LINT", 0xCA => "REAL", 0xC6 => "USINT", 0xC7 => "UINT", 0xC8 => "UDINT", 0xC9 => "ULINT", 0xC0 => "DT", 0xD7 => "TIME", 0xCB => "LREAL", 0xD6 => "TIME32", 0xDF => "LTIME", 0xCC => "LDT", _ => null };
        if ((type & 0x8000) == 0) return atomic is null
            ? new(name, path, $"CIP:0x{type:X4}", "Unknown", [], false, "Unsupported CIP type.")
            : new(name, path, atomic, "Unknown", []);
        if ((type & 0x1000) != 0) return new(name, path, "SystemType", "Unknown", [], false, "System templates require an offline export.");
        var template = await TemplateAsync(type & 0xFFF, cancellationToken).ConfigureAwait(false);
        var visible = template.Members.Where(m => !m.Name.StartsWith("ZZZZ", StringComparison.Ordinal)).ToArray();
        var len = visible.SingleOrDefault(m => m.Name == "LEN" && (m.Type & 0x7FFF) == 0xC4);
        var data = visible.SingleOrDefault(m => m.Name == "DATA" && (m.Type & 0x9FFF) == 0xC2 && ((m.Type >> 13) & 3) == 1);
        if (visible.Length == 2 && len != null && data != null && visible[0] == len && visible[1] == data && data.Metadata > 0)
            return new(name, path, template.Name, "Unknown", [], StringCapacity: data.Metadata);
        var children = new List<TagNode>();
        foreach (var member in template.Members)
        {
            if (member.Name.StartsWith("ZZZZ", StringComparison.Ordinal)) continue; // compiler's hidden BOOL storage
            int dimensionsCount = (member.Type >> 13) & 3;
            if (dimensionsCount > 1) { children.Add(new(member.Name, path + "." + member.Name, "Array", "Unknown", [], true, "Logix UDT array members support one dimension.")); continue; }
            children.Add(await BuildAsync(member.Name, path + "." + member.Name, member.Type,
                dimensionsCount == 1 ? [(uint)member.Metadata] : [], depth + 1, cancellationToken).ConfigureAwait(false));
        }
        return new(name, path, template.Name, "Unknown", children, false, children.Count == 0 ? "Empty template." : null);
    }
    private static ushort U16(byte[] b, int offset) => BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(offset));
    private static uint U32(byte[] b, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(offset));
    private static string ReadCString(byte[] bytes, ref int offset)
    {
        int end = Array.IndexOf(bytes, (byte)0, offset);
        if (end < 0) throw new FormatException("Unterminated UDT name.");
        string value = Encoding.ASCII.GetString(bytes, offset, end - offset); offset = end + 1; return value;
    }
}
