using System.Buffers.Binary;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace LogixTagRelay;

public enum Direction { PlcToApp, AppToPlc }
public enum PlcType { BOOL, SINT, INT, DINT, LINT, REAL, STRING }
public enum Quality { Unknown, Good, Bad }

public static class PlcTypes
{
    public static int Size(PlcType type, int stringCapacity = 0) => type switch
    {
        PlcType.BOOL => 4, // Deliberate wire representation: DINT, never CLR/Logix BOOL layout.
        PlcType.SINT => 1, PlcType.INT => 2, PlcType.DINT or PlcType.REAL => 4,
        PlcType.LINT => 8, PlcType.STRING when stringCapacity is >= 1 and <= 65535 => 4 + stringCapacity,
        _ => throw new ArgumentOutOfRangeException(nameof(type), "A STRING requires its declared capacity (1..65535).")
    };
    public static string CSharp(PlcType type) => type switch
    {
        PlcType.BOOL => "bool", PlcType.SINT => "sbyte", PlcType.INT => "short",
        PlcType.DINT => "int", PlcType.LINT => "long", PlcType.REAL => "float", PlcType.STRING => "string",
        _ => throw new ArgumentOutOfRangeException(nameof(type))
    };
    public static void ValidateString(string value, int capacity)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (capacity is < 1 or > 65535 || value.Length > capacity)
            throw new ArgumentException($"STRING exceeds its declared {capacity}-byte capacity.");
        if (value.Any(c => c > 255)) throw new ArgumentException("Logix byte strings use a lossless Latin-1 mapping; characters above U+00FF are unsupported.");
    }
    public static void Encode(FieldMap field, object value, Span<byte> destination) => Encode(field.Type, value, destination, field.StringCapacity);
    public static object Decode(FieldMap field, ReadOnlySpan<byte> source) => Decode(field.Type, source, field.StringCapacity);
    public static void Encode(PlcType type, object value, Span<byte> destination, int stringCapacity = 0)
    {
        switch (type)
        {
            case PlcType.BOOL: BinaryPrimitives.WriteInt32LittleEndian(destination, (bool)value ? 1 : 0); break;
            case PlcType.SINT: destination[0] = unchecked((byte)(sbyte)value); break;
            case PlcType.INT: BinaryPrimitives.WriteInt16LittleEndian(destination, (short)value); break;
            case PlcType.DINT: BinaryPrimitives.WriteInt32LittleEndian(destination, (int)value); break;
            case PlcType.LINT: BinaryPrimitives.WriteInt64LittleEndian(destination, (long)value); break;
            case PlcType.REAL: BinaryPrimitives.WriteSingleLittleEndian(destination, (float)value); break;
            case PlcType.STRING:
                var text = (string)value;
                ValidateString(text, stringCapacity);
                destination[..Size(type, stringCapacity)].Clear();
                BinaryPrimitives.WriteInt32LittleEndian(destination, text.Length);
                for (int i = 0; i < text.Length; i++) destination[4 + i] = (byte)text[i];
                break;
            default: throw new ArgumentOutOfRangeException(nameof(type));
        }
    }
    public static object Decode(PlcType type, ReadOnlySpan<byte> source, int stringCapacity = 0) => type switch
    {
        PlcType.BOOL => BinaryPrimitives.ReadInt32LittleEndian(source) != 0,
        PlcType.SINT => unchecked((sbyte)source[0]), PlcType.INT => BinaryPrimitives.ReadInt16LittleEndian(source),
        PlcType.DINT => BinaryPrimitives.ReadInt32LittleEndian(source),
        PlcType.LINT => BinaryPrimitives.ReadInt64LittleEndian(source),
        PlcType.REAL => BinaryPrimitives.ReadSingleLittleEndian(source),
        PlcType.STRING => DecodeString(source, stringCapacity),
        _ => throw new ArgumentOutOfRangeException(nameof(type))
    };
    private static string DecodeString(ReadOnlySpan<byte> source, int capacity)
    {
        if (source.Length < Size(PlcType.STRING, capacity)) throw new FormatException("Truncated STRING image.");
        int length = BinaryPrimitives.ReadInt32LittleEndian(source);
        if (length < 0 || length > capacity) throw new FormatException("STRING LEN is outside its declared DATA capacity.");
        return System.Text.Encoding.Latin1.GetString(source.Slice(4, length));
    }
}

public sealed record FieldMap(string Path, PlcType Type, Direction Direction, int Offset, string Access = "Read/Write", int StringCapacity = 0)
{
    [JsonIgnore] public int WireBytes => PlcTypes.Size(Type, StringCapacity);
}
public sealed record ExplicitOptions(int IntervalMs = 100, string? TriggerTag = null, int TriggerPollMs = 20,
    string? AcknowledgeTag = null, string? HandledTriggerTag = null, string? ResponseTag = null)
{
    // AcknowledgeTag is the original API name for the exact handled-trigger mirror.
    [JsonIgnore] public string? MirrorTag => HandledTriggerTag ?? AcknowledgeTag;
}
public sealed record ImplicitOptions(int PlcInputAssembly = 101, int PlcOutputAssembly = 100,
    int ConfigurationAssembly = 102, int RpiMs = 20, int MaxAssemblyBytes = 480);
public sealed record SyncConfiguration(string SchemaHash, List<FieldMap> Fields,
    int PlcToAppBytes, int AppToPlcBytes, string Gateway = "192.168.1.10", string Route = "1,0",
    ExplicitOptions? Explicit = null, ImplicitOptions? Implicit = null, int SchemaVersion = 2)
{
    public static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() }, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };
    public string ToJson() => JsonSerializer.Serialize(this, Json) + "\n";
    public static SyncConfiguration FromJson(string json)
    {
        using var doc = JsonDocument.Parse(json);
        CheckUnique(doc.RootElement);
        var result = JsonSerializer.Deserialize<SyncConfiguration>(json, Json)
            ?? throw new FormatException("Empty configuration.");
        result.Validate();
        return result;
    }
    private static void CheckUnique(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var keys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!keys.Add(property.Name)) throw new FormatException($"Duplicate JSON key: {property.Name}");
                CheckUnique(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var item in element.EnumerateArray()) CheckUnique(item);
    }
    public void Validate()
    {
        if (SchemaVersion != 2 || Fields is null || Fields.Count == 0 || string.IsNullOrWhiteSpace(SchemaHash))
            throw new FormatException("A version 2 mapping with fields and schema hash is required. Regenerate older contracts.");
        if (PlcToAppBytes < 24 || AppToPlcBytes < 24 || PlcToAppBytes % 4 != 0 || AppToPlcBytes % 4 != 0)
            throw new FormatException("Assembly sizes must be at least 24 and divisible by 4.");
        if (PlcToAppBytes != AppToPlcBytes) throw new FormatException("Both directions must use the same complete contract image size.");
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var field in Fields)
        {
            if (!Enum.IsDefined(field.Type) || !Enum.IsDefined(field.Direction) || !paths.Add(field.Path)
                || field.Offset < 24 || field.Offset % 4 != 0 ||
                !Regex.IsMatch(field.Path, @"^(?:Program:[A-Za-z_][A-Za-z0-9_]*\.)?[A-Za-z_][A-Za-z0-9_]*(?:\[\d+(?:,\d+){0,2}\])?(?:\.[A-Za-z_][A-Za-z0-9_]*(?:\[\d+(?:,\d+){0,2}\])?)*$"))
                throw new FormatException("Invalid or duplicate field mapping.");
            if ((field.Type == PlcType.STRING && field.StringCapacity is < 1 or > 65535) ||
                (field.Type != PlcType.STRING && field.StringCapacity != 0)) throw new FormatException("Invalid STRING capacity metadata.");
            if (field.Access == "None" || (field.Direction == Direction.AppToPlc && field.Access != "Read/Write"))
                throw new FormatException($"External access prevents mapping {field.Path}.");
            int size = field.Direction == Direction.PlcToApp ? PlcToAppBytes : AppToPlcBytes;
            if ((long)field.Offset + field.WireBytes > size) throw new FormatException("Field exceeds assembly.");
        }
        var ordered = Fields.OrderBy(x => x.Offset).ToArray();
        for (int i = 1; i < ordered.Length; i++)
        {
            if (ordered[i - 1].Offset + ordered[i - 1].WireBytes > ordered[i].Offset)
                throw new FormatException("Overlapping field mappings in the shared image.");
        }
        if (Explicit is { } e)
        {
            if (e.IntervalMs <= 0 || e.TriggerPollMs <= 0 ||
                (e.TriggerTag == null && (e.MirrorTag != null || e.ResponseTag != null)) ||
                (e.AcknowledgeTag != null && e.HandledTriggerTag != null && !e.AcknowledgeTag.Equals(e.HandledTriggerTag, StringComparison.OrdinalIgnoreCase)))
                throw new FormatException("Invalid explicit trigger/mirror/response settings.");
            var controls = new[] { e.TriggerTag, e.MirrorTag, e.ResponseTag }.Where(p => p != null).Cast<string>().ToArray();
            if (controls.Distinct(StringComparer.OrdinalIgnoreCase).Count() != controls.Length || controls.Any(p =>
                !Regex.IsMatch(p, @"^(?:Program:[A-Za-z_][A-Za-z0-9_]*\.)?[A-Za-z_][A-Za-z0-9_]*(?:\[\d+(?:,\d+){0,2}\])?(?:\.[A-Za-z_][A-Za-z0-9_]*(?:\[\d+(?:,\d+){0,2}\])?)*$") ||
                Fields.Any(f => f.Path.Equals(p, StringComparison.OrdinalIgnoreCase))))
                throw new FormatException("Trigger, mirror and response tags must be valid, distinct DINT addresses outside the data mapping.");
        }
        if (Implicit is { } io && (io.RpiMs <= 0 || io.MaxAssemblyBytes < 24 || io.MaxAssemblyBytes > 480 ||
            PlcToAppBytes > io.MaxAssemblyBytes || AppToPlcBytes > io.MaxAssemblyBytes ||
            io.PlcInputAssembly <= 0 || io.PlcOutputAssembly <= 0 || io.ConfigurationAssembly <= 0 ||
            io.PlcInputAssembly > ushort.MaxValue || io.PlcOutputAssembly > ushort.MaxValue || io.ConfigurationAssembly > ushort.MaxValue ||
            new[] { io.PlcInputAssembly, io.PlcOutputAssembly, io.ConfigurationAssembly }.Distinct().Count() != 3))
            throw new FormatException("Invalid assembly parameters or payload too large; split into groups.");
        if (ComputeHash(Fields) != SchemaHash) throw new FormatException("Schema hash does not match fields.");
    }
    public static string ComputeHash(IEnumerable<FieldMap> fields)
    {
        string canonical = string.Join("\n", fields.Select(f =>
            string.Join("|", f.Path, f.Type, f.Direction, f.Offset.ToString(CultureInfo.InvariantCulture), f.Access, f.StringCapacity.ToString(CultureInfo.InvariantCulture))));
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(canonical)));
    }
}
