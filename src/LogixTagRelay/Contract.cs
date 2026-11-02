using System.Buffers.Binary;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace LogixTagRelay;

public enum Direction { PlcToApp, AppToPlc }
public enum PlcType { BOOL, SINT, INT, DINT, LINT, REAL }
public enum Quality { Unknown, Good, Bad }

public static class PlcTypes
{
    public static int Size(PlcType type) => type switch
    {
        PlcType.BOOL => 4, // Deliberate wire representation: DINT, never CLR/Logix BOOL layout.
        PlcType.SINT => 1, PlcType.INT => 2, PlcType.DINT or PlcType.REAL => 4,
        PlcType.LINT => 8, _ => throw new ArgumentOutOfRangeException(nameof(type))
    };
    public static string CSharp(PlcType type) => type switch
    {
        PlcType.BOOL => "bool", PlcType.SINT => "sbyte", PlcType.INT => "short",
        PlcType.DINT => "int", PlcType.LINT => "long", PlcType.REAL => "float",
        _ => throw new ArgumentOutOfRangeException(nameof(type))
    };
    public static void Encode(PlcType type, object value, Span<byte> destination)
    {
        switch (type)
        {
            case PlcType.BOOL: BinaryPrimitives.WriteInt32LittleEndian(destination, (bool)value ? 1 : 0); break;
            case PlcType.SINT: destination[0] = unchecked((byte)(sbyte)value); break;
            case PlcType.INT: BinaryPrimitives.WriteInt16LittleEndian(destination, (short)value); break;
            case PlcType.DINT: BinaryPrimitives.WriteInt32LittleEndian(destination, (int)value); break;
            case PlcType.LINT: BinaryPrimitives.WriteInt64LittleEndian(destination, (long)value); break;
            case PlcType.REAL: BinaryPrimitives.WriteSingleLittleEndian(destination, (float)value); break;
            default: throw new ArgumentOutOfRangeException(nameof(type));
        }
    }
    public static object Decode(PlcType type, ReadOnlySpan<byte> source) => type switch
    {
        PlcType.BOOL => BinaryPrimitives.ReadInt32LittleEndian(source) != 0,
        PlcType.SINT => unchecked((sbyte)source[0]), PlcType.INT => BinaryPrimitives.ReadInt16LittleEndian(source),
        PlcType.DINT => BinaryPrimitives.ReadInt32LittleEndian(source),
        PlcType.LINT => BinaryPrimitives.ReadInt64LittleEndian(source),
        PlcType.REAL => BinaryPrimitives.ReadSingleLittleEndian(source),
        _ => throw new ArgumentOutOfRangeException(nameof(type))
    };
}

public sealed record FieldMap(string Path, PlcType Type, Direction Direction, int Offset, string Access = "Read/Write");
public sealed record ExplicitOptions(int IntervalMs = 100, string? TriggerTag = null, int TriggerPollMs = 20,
    string? AcknowledgeTag = null);
public sealed record ImplicitOptions(int PlcInputAssembly = 101, int PlcOutputAssembly = 100,
    int ConfigurationAssembly = 102, int RpiMs = 20, int MaxAssemblyBytes = 480);
public sealed record SyncConfiguration(string SchemaHash, List<FieldMap> Fields,
    int PlcToAppBytes, int AppToPlcBytes, string Gateway = "192.168.1.10", string Route = "1,0",
    ExplicitOptions? Explicit = null, ImplicitOptions? Implicit = null, int SchemaVersion = 1)
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
        if (SchemaVersion != 1 || Fields is null || Fields.Count == 0 || string.IsNullOrWhiteSpace(SchemaHash))
            throw new FormatException("A version 1 mapping with fields and schema hash is required.");
        if (PlcToAppBytes < 24 || AppToPlcBytes < 24 || PlcToAppBytes % 4 != 0 || AppToPlcBytes % 4 != 0)
            throw new FormatException("Assembly sizes must be at least 24 and divisible by 4.");
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var field in Fields)
        {
            if (!Enum.IsDefined(field.Type) || !Enum.IsDefined(field.Direction) || !paths.Add(field.Path)
                || field.Offset < 24 || field.Offset % 4 != 0 ||
                !Regex.IsMatch(field.Path, @"^(?:Program:[A-Za-z_][A-Za-z0-9_]*\.)?[A-Za-z_][A-Za-z0-9_]*(?:\[\d+\])?(?:\.[A-Za-z_][A-Za-z0-9_]*(?:\[\d+\])?)*$"))
                throw new FormatException("Invalid or duplicate field mapping.");
            if (field.Access == "None" || (field.Direction == Direction.AppToPlc && field.Access != "Read/Write"))
                throw new FormatException($"External access prevents mapping {field.Path}.");
            int size = field.Direction == Direction.PlcToApp ? PlcToAppBytes : AppToPlcBytes;
            if ((long)field.Offset + PlcTypes.Size(field.Type) > size) throw new FormatException("Field exceeds assembly.");
        }
        foreach (var direction in Enum.GetValues<Direction>())
        {
            var ordered = Fields.Where(x => x.Direction == direction).OrderBy(x => x.Offset).ToArray();
            for (int i = 1; i < ordered.Length; i++)
                if (ordered[i - 1].Offset + PlcTypes.Size(ordered[i - 1].Type) > ordered[i].Offset)
                    throw new FormatException("Overlapping field mappings.");
        }
        if (Explicit is { } e && (e.IntervalMs <= 0 || e.TriggerPollMs <= 0 ||
            (e.AcknowledgeTag != null && e.TriggerTag == null) ||
            (e.TriggerTag != null && (Fields.Any(f => f.Path.Equals(e.TriggerTag, StringComparison.OrdinalIgnoreCase)) ||
                string.Equals(e.TriggerTag, e.AcknowledgeTag, StringComparison.OrdinalIgnoreCase))) ||
            (e.AcknowledgeTag != null && Fields.Any(f => f.Path.Equals(e.AcknowledgeTag, StringComparison.OrdinalIgnoreCase)))))
            throw new FormatException("Invalid explicit schedule or trigger/ack overlaps a mapped field.");
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
            string.Join("|", f.Path, f.Type, f.Direction, f.Offset.ToString(CultureInfo.InvariantCulture), f.Access)));
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(canonical)));
    }
}
