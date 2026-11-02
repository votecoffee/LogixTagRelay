using System.Buffers.Binary;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace LogixTagRelay;

/// <summary>Selects how PLC observations and application edits participate in synchronization.</summary>
public enum Direction
{
    /// <summary>Refreshes the application from the PLC; application assignment is rejected.</summary>
    PlcToApp,
    /// <summary>Writes application edits and retains separate PLC readback without replacing the desired value.</summary>
    AppToPlc,
    /// <summary>Refreshes clean values from the PLC and writes pending application edits.</summary>
    Bidirectional,
}
/// <summary>Supported PLC scalars. The relay wire layout is distinct from native UDT storage.</summary>
public enum PlcType
{
    /// <summary>Boolean, encoded as a four-byte DINT in the relay image.</summary>
    BOOL,
    /// <summary>Signed eight-bit integer.</summary>
    SINT,
    /// <summary>Signed 16-bit integer.</summary>
    INT,
    /// <summary>Signed 32-bit integer.</summary>
    DINT,
    /// <summary>Signed 64-bit integer.</summary>
    LINT,
    /// <summary>IEEE 754 single-precision floating point.</summary>
    REAL,
    /// <summary>Counted Latin-1 bytes with a declared DATA capacity and four-byte LEN.</summary>
    STRING,
    /// <summary>Unsigned eight-bit integer.</summary>
    USINT,
    /// <summary>Unsigned 16-bit integer.</summary>
    UINT,
    /// <summary>Unsigned 32-bit integer.</summary>
    UDINT,
    /// <summary>Unsigned 64-bit integer.</summary>
    ULINT,
    /// <summary>Signed 64-bit microseconds since the Unix epoch, represented by PlcDateTime.</summary>
    DT,
    /// <summary>Signed 64-bit microseconds of duration, represented by PlcTime.</summary>
    TIME,
    /// <summary>IEEE 754 double-precision floating point.</summary>
    LREAL,
    /// <summary>Signed 32-bit microseconds of duration, represented by PlcTime32.</summary>
    TIME32,
    /// <summary>Signed 64-bit nanoseconds of duration, represented by PlcLongTime.</summary>
    LTIME,
    /// <summary>Signed 64-bit nanoseconds since the Unix epoch, represented by PlcLongDateTime.</summary>
    LDT,
}
/// <summary>Last synchronization outcome for an application variable.</summary>
public enum Quality
{
    /// <summary>No successful synchronization is recorded.</summary>
    Unknown,
    /// <summary>The last accepted observation or write succeeded.</summary>
    Good,
    /// <summary>A synchronization or freshness check failed.</summary>
    Bad,
}

/// <summary>Lossless scalar codecs for the relay&apos;s little-endian wire layout.</summary>
public static class PlcTypes
{
    /// <summary>Returns the encoded scalar byte count; STRING requires its declared capacity.</summary>
    /// <param name="type">Supported PLC scalar type.</param>
    /// <param name="stringCapacity">Declared STRING DATA capacity in bytes; zero for non-STRING scalars.</param>
    public static int Size(PlcType type, int stringCapacity = 0) => type switch
    {
        PlcType.BOOL => 4, // Deliberate wire representation: DINT, never CLR/Logix BOOL layout.
        PlcType.SINT or PlcType.USINT => 1, PlcType.INT or PlcType.UINT => 2, PlcType.DINT or PlcType.UDINT or PlcType.REAL or PlcType.TIME32 => 4,
        PlcType.LINT or PlcType.ULINT or PlcType.DT or PlcType.TIME or PlcType.LREAL or PlcType.LTIME or PlcType.LDT => 8, PlcType.STRING when stringCapacity is >= 1 and <= 65535 => 4 + stringCapacity,
        _ => throw new ArgumentOutOfRangeException(nameof(type), "A STRING requires its declared capacity (1..65535).")
    };
    /// <summary>Returns the C# type name used by generated contracts.</summary>
    /// <param name="type">Supported PLC scalar type.</param>
    public static string CSharp(PlcType type) => type switch
    {
        PlcType.BOOL => "bool", PlcType.SINT => "sbyte", PlcType.INT => "short",
        PlcType.USINT => "byte", PlcType.UINT => "ushort", PlcType.UDINT => "uint", PlcType.ULINT => "ulong",
        PlcType.LREAL => "double", PlcType.TIME32 => "PlcTime32", PlcType.LTIME => "PlcLongTime", PlcType.LDT => "PlcLongDateTime",
        PlcType.DT => "PlcDateTime", PlcType.TIME => "PlcTime",
        PlcType.DINT => "int", PlcType.LINT => "long", PlcType.REAL => "float", PlcType.STRING => "string",
        _ => throw new ArgumentOutOfRangeException(nameof(type))
    };
    /// <summary>Rejects null text, invalid capacities, capacity overflow and characters above U+00FF.</summary>
    /// <param name="value">Typed scalar or timestamp value required by this operation.</param>
    /// <param name="capacity">Declared STRING DATA capacity in bytes, from 1 through 65535.</param>
    public static void ValidateString(string value, int capacity)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (capacity is < 1 or > 65535 || value.Length > capacity)
            throw new ArgumentException($"STRING exceeds its declared {capacity}-byte capacity.");
        if (value.Any(c => c > 255)) throw new ArgumentException("Logix byte strings use a lossless Latin-1 mapping; characters above U+00FF are unsupported.");
    }
    /// <summary>Encodes a typed scalar into the relay layout; the destination must have at least the required wire size.</summary>
    /// <param name="field">Scalar mapping, including STRING capacity when applicable.</param>
    /// <param name="value">Typed scalar or timestamp value required by this operation.</param>
    /// <param name="destination">Writable buffer large enough for the encoded scalar.</param>
    public static void Encode(FieldMap field, object value, Span<byte> destination) => Encode(field.Type, value, destination, field.StringCapacity);
    /// <summary>Decodes a relay scalar, validating counted STRING length against capacity.</summary>
    /// <param name="field">Scalar mapping, including STRING capacity when applicable.</param>
    /// <param name="source">Source buffer or text to be processed by this operation.</param>
    public static object Decode(FieldMap field, ReadOnlySpan<byte> source) => Decode(field.Type, source, field.StringCapacity);
    /// <summary>Encodes a typed scalar into the relay layout; the destination must have at least the required wire size.</summary>
    /// <param name="type">Supported PLC scalar type.</param>
    /// <param name="value">Typed scalar or timestamp value required by this operation.</param>
    /// <param name="destination">Writable buffer large enough for the encoded scalar.</param>
    /// <param name="stringCapacity">Declared STRING DATA capacity in bytes; zero for non-STRING scalars.</param>
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
            case PlcType.USINT: destination[0] = (byte)value; break;
            case PlcType.UINT: BinaryPrimitives.WriteUInt16LittleEndian(destination, (ushort)value); break;
            case PlcType.UDINT: BinaryPrimitives.WriteUInt32LittleEndian(destination, (uint)value); break;
            case PlcType.ULINT: BinaryPrimitives.WriteUInt64LittleEndian(destination, (ulong)value); break;
            case PlcType.DT: BinaryPrimitives.WriteInt64LittleEndian(destination, ((PlcDateTime)value).UnixMicroseconds); break;
            case PlcType.TIME: BinaryPrimitives.WriteInt64LittleEndian(destination, ((PlcTime)value).Microseconds); break;
            case PlcType.LREAL: BinaryPrimitives.WriteDoubleLittleEndian(destination, (double)value); break;
            case PlcType.TIME32: BinaryPrimitives.WriteInt32LittleEndian(destination, ((PlcTime32)value).Microseconds); break;
            case PlcType.LTIME: BinaryPrimitives.WriteInt64LittleEndian(destination, ((PlcLongTime)value).Nanoseconds); break;
            case PlcType.LDT: BinaryPrimitives.WriteInt64LittleEndian(destination, ((PlcLongDateTime)value).UnixNanoseconds); break;
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
    /// <summary>Decodes a relay scalar, validating counted STRING length against capacity.</summary>
    /// <param name="type">Supported PLC scalar type.</param>
    /// <param name="source">Source buffer or text to be processed by this operation.</param>
    /// <param name="stringCapacity">Declared STRING DATA capacity in bytes; zero for non-STRING scalars.</param>
    public static object Decode(PlcType type, ReadOnlySpan<byte> source, int stringCapacity = 0) => type switch
    {
        PlcType.BOOL => BinaryPrimitives.ReadInt32LittleEndian(source) != 0,
        PlcType.SINT => unchecked((sbyte)source[0]), PlcType.INT => BinaryPrimitives.ReadInt16LittleEndian(source),
        PlcType.DINT => BinaryPrimitives.ReadInt32LittleEndian(source),
        PlcType.LINT => BinaryPrimitives.ReadInt64LittleEndian(source),
        PlcType.REAL => BinaryPrimitives.ReadSingleLittleEndian(source),
        PlcType.USINT => source[0], PlcType.UINT => BinaryPrimitives.ReadUInt16LittleEndian(source),
        PlcType.UDINT => BinaryPrimitives.ReadUInt32LittleEndian(source), PlcType.ULINT => BinaryPrimitives.ReadUInt64LittleEndian(source),
        PlcType.DT => new PlcDateTime(BinaryPrimitives.ReadInt64LittleEndian(source)),
        PlcType.TIME => new PlcTime(BinaryPrimitives.ReadInt64LittleEndian(source)),
        PlcType.LREAL => BinaryPrimitives.ReadDoubleLittleEndian(source),
        PlcType.TIME32 => new PlcTime32(BinaryPrimitives.ReadInt32LittleEndian(source)),
        PlcType.LTIME => new PlcLongTime(BinaryPrimitives.ReadInt64LittleEndian(source)),
        PlcType.LDT => new PlcLongDateTime(BinaryPrimitives.ReadInt64LittleEndian(source)),
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

/// <summary>One scalar member&apos;s PLC path, access permissions and offset in the symmetric relay image.</summary>
/// <param name="Path">Fully qualified Logix path including optional program scope and array coordinates.</param>
/// <param name="Type">Supported PLC scalar type.</param>
/// <param name="Direction">Requested synchronization direction.</param>
/// <param name="Offset">Four-byte-aligned scalar byte offset after the 24-byte relay header.</param>
/// <param name="Access">Controller external access metadata, such as Read/Write or Read Only.</param>
/// <param name="StringCapacity">Declared STRING DATA capacity, or zero for other scalar types.</param>
public sealed record FieldMap(string Path, PlcType Type, Direction Direction, int Offset, string Access = "Read/Write", int StringCapacity = 0)
{
    /// <summary>Encoded scalar size, excluding alignment padding.</summary>
    [JsonIgnore] public int WireBytes => PlcTypes.Size(Type, StringCapacity);
    /// <summary>Whether both the direction and PLC external access permit application writes.</summary>
    [JsonIgnore] public bool CanWrite => (Direction is Direction.AppToPlc or Direction.Bidirectional) && Access == "Read/Write";
}
/// <summary>A native UDT root read and the mapped scalar paths decoded from that snapshot.</summary>
/// <param name="Path">Fully qualified Logix path including optional program scope and array coordinates.</param>
/// <param name="DataType">Native Logix type name of the UDT root.</param>
/// <param name="Fields">Ordered scalar field mappings or paths included in this group.</param>
public sealed record ExplicitReadGroup(string Path, string DataType, List<string> Fields);
/// <summary>Cyclic or DINT-triggered explicit update settings, with optional handled mirror and signed DINT response.</summary>
/// <param name="IntervalMs">Cyclic explicit update interval in milliseconds; defaults to 100.</param>
/// <param name="TriggerTag">Optional DINT request tag; a changed nonzero value requests a transfer.</param>
/// <param name="TriggerPollMs">Polling interval for the DINT request, in milliseconds.</param>
/// <param name="AcknowledgeTag">Legacy name for the optional exact handled-trigger DINT mirror.</param>
/// <param name="HandledTriggerTag">Optional exact handled-trigger DINT mirror; takes precedence over AcknowledgeTag.</param>
/// <param name="ResponseTag">Optional signed DINT reply: positive request magnitude for ACK, negative for NACK.</param>
/// <param name="ReadGroups">Optional whole-UDT native read groups with scalar member decoding.</param>
public sealed record ExplicitOptions(int IntervalMs = 100, string? TriggerTag = null, int TriggerPollMs = 20,
    string? AcknowledgeTag = null, string? HandledTriggerTag = null, string? ResponseTag = null,
    List<ExplicitReadGroup>? ReadGroups = null)
{
    // AcknowledgeTag is the original API name for the exact handled-trigger mirror.
    /// <summary>Effective handled-trigger mirror; HandledTriggerTag takes precedence over AcknowledgeTag.</summary>
    [JsonIgnore] public string? MirrorTag => HandledTriggerTag ?? AcknowledgeTag;
}
/// <summary>Assembly instances and timing for one symmetric image, capped at 480 bytes including its header.</summary>
/// <param name="PlcInputAssembly">Adapter assembly consumed as the PLC module&apos;s input.</param>
/// <param name="PlcOutputAssembly">Adapter assembly produced by the PLC module&apos;s output.</param>
/// <param name="ConfigurationAssembly">Adapter configuration assembly instance.</param>
/// <param name="RpiMs">Requested packet interval in milliseconds.</param>
/// <param name="MaxAssemblyBytes">Maximum symmetric image size including the header; at most 480 bytes.</param>
public sealed record ImplicitOptions(int PlcInputAssembly = 101, int PlcOutputAssembly = 100,
    int ConfigurationAssembly = 102, int RpiMs = 20, int MaxAssemblyBytes = 480);
/// <summary>Explicit-only BOOL heartbeat, toggled every five seconds by default; consumes no implicit image bytes.</summary>
/// <param name="Tag">Existing externally writable BOOL outside the data contract, updated through explicit communication.</param>
/// <param name="IntervalMs">Update interval in milliseconds. Heartbeat defaults to a five-second explicit BOOL toggle.</param>
public sealed record HeartbeatOptions(string Tag, int IntervalMs = 5000);
/// <summary>Versioned contract layout and communication settings. Regenerate PLC and C# peers together after layout changes.</summary>
/// <param name="SchemaHash">SHA-256 identity of the ordered scalar layout.</param>
/// <param name="Fields">Ordered scalar field mappings or paths included in this group.</param>
/// <param name="PlcToAppBytes">Complete PLC-to-application image length including its header.</param>
/// <param name="AppToPlcBytes">Complete application-to-PLC image length; must equal PlcToAppBytes.</param>
/// <param name="Gateway">PLC endpoint hostname or IP address.</param>
/// <param name="Route">CIP route to the intended controller.</param>
/// <param name="Explicit">Optional explicit interval, trigger, receipt and grouped-read settings.</param>
/// <param name="Implicit">Optional implicit assembly and timing settings.</param>
/// <param name="SchemaVersion">Contract configuration format version; currently 2.</param>
/// <param name="Heartbeat">Optional explicit-only BOOL heartbeat, independent of implicit payload layout.</param>
public sealed record SyncConfiguration(string SchemaHash, List<FieldMap> Fields,
    int PlcToAppBytes, int AppToPlcBytes, string Gateway = "192.168.1.10", string Route = "1,0",
    ExplicitOptions? Explicit = null, ImplicitOptions? Implicit = null, int SchemaVersion = 2, HeartbeatOptions? Heartbeat = null)
{
    /// <summary>Shared strict JSON settings for generated contract configuration.</summary>
    public static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() }, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };
    /// <summary>Serializes the configuration or project using the relay JSON format.</summary>
    public string ToJson() => JsonSerializer.Serialize(this, Json) + "\n";
    /// <summary>Parses JSON, rejecting duplicate keys and unknown properties. Contract configurations also undergo layout validation.</summary>
    /// <param name="json">Serialized contract or project JSON.</param>
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
    /// <summary>Validates schema identity, layout, access, image sizes and control-tag settings; throws FormatException for invalid contracts.</summary>
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
            if (field.Access == "None" || (field.Direction != Direction.PlcToApp && field.Access != "Read/Write"))
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
            ExplicitReads.Validate(Fields, e.ReadGroups ?? []);
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
        if (Heartbeat is { } heartbeat)
        {
            if (heartbeat.IntervalMs <= 0 || !Regex.IsMatch(heartbeat.Tag ?? "", @"^(?:Program:[A-Za-z_][A-Za-z0-9_]*\.)?[A-Za-z_][A-Za-z0-9_]*(?:\[\d+(?:,\d+){0,2}\])?(?:\.[A-Za-z_][A-Za-z0-9_]*(?:\[\d+(?:,\d+){0,2}\])?)*$")) throw new FormatException("Heartbeat requires a valid BOOL tag and positive interval.");
            var field = Fields.SingleOrDefault(f => f.Path.Equals(heartbeat.Tag, StringComparison.OrdinalIgnoreCase));
            if (field is not null) throw new FormatException("Heartbeat is an explicit BOOL control tag outside the contract data mapping.");
            if (new[] { Explicit?.TriggerTag, Explicit?.MirrorTag, Explicit?.ResponseTag }.Any(p => string.Equals(p, heartbeat.Tag, StringComparison.OrdinalIgnoreCase)))
                throw new FormatException("Heartbeat cannot share a trigger or reply address.");
        }
        if (Implicit is { } io && (io.RpiMs <= 0 || io.MaxAssemblyBytes < 24 || io.MaxAssemblyBytes > 480 ||
            PlcToAppBytes > io.MaxAssemblyBytes || AppToPlcBytes > io.MaxAssemblyBytes ||
            io.PlcInputAssembly <= 0 || io.PlcOutputAssembly <= 0 || io.ConfigurationAssembly <= 0 ||
            io.PlcInputAssembly > ushort.MaxValue || io.PlcOutputAssembly > ushort.MaxValue || io.ConfigurationAssembly > ushort.MaxValue ||
            new[] { io.PlcInputAssembly, io.PlcOutputAssembly, io.ConfigurationAssembly }.Distinct().Count() != 3))
            throw new FormatException("Invalid assembly parameters or payload too large; split into groups.");
        if (ComputeHash(Fields) != SchemaHash) throw new FormatException("Schema hash does not match fields.");
    }
    /// <summary>Computes a deterministic SHA-256 layout identity from the ordered scalar field mappings.</summary>
    /// <param name="fields">Ordered scalar mappings to validate, encode or read.</param>
    public static string ComputeHash(IEnumerable<FieldMap> fields)
    {
        string canonical = string.Join("\n", fields.Select(f =>
            string.Join("|", f.Path, f.Type, f.Direction, f.Offset.ToString(CultureInfo.InvariantCulture), f.Access, f.StringCapacity.ToString(CultureInfo.InvariantCulture))));
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(canonical)));
    }
}
