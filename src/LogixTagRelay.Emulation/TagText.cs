using System.Globalization;

namespace LogixTagRelay.Emulation;

/// <summary>Lossless text boundary for the GUI and HTTP transport, including full LINT precision.</summary>
public static class TagText
{
    public static object Parse(FieldMap field, string value)
    {
        object parsed = field.Type switch
        {
            PlcType.BOOL => bool.Parse(value),
            PlcType.SINT => sbyte.Parse(value, NumberStyles.Integer, CultureInfo.InvariantCulture),
            PlcType.INT => short.Parse(value, NumberStyles.Integer, CultureInfo.InvariantCulture),
            PlcType.DINT => int.Parse(value, NumberStyles.Integer, CultureInfo.InvariantCulture),
            PlcType.LINT => long.Parse(value, NumberStyles.Integer, CultureInfo.InvariantCulture),
            PlcType.USINT => byte.Parse(value, NumberStyles.Integer, CultureInfo.InvariantCulture),
            PlcType.UINT => ushort.Parse(value, NumberStyles.Integer, CultureInfo.InvariantCulture),
            PlcType.UDINT => uint.Parse(value, NumberStyles.Integer, CultureInfo.InvariantCulture),
            PlcType.ULINT => ulong.Parse(value, NumberStyles.Integer, CultureInfo.InvariantCulture),
            PlcType.DT => new PlcDateTime(long.Parse(value, NumberStyles.Integer, CultureInfo.InvariantCulture)),
            PlcType.TIME => new PlcTime(long.Parse(value, NumberStyles.Integer, CultureInfo.InvariantCulture)),
            PlcType.REAL => float.Parse(value, NumberStyles.Float, CultureInfo.InvariantCulture),
            PlcType.STRING => value,
            _ => throw new ArgumentOutOfRangeException(nameof(field))
        };
        PlcTypes.Encode(field, parsed, new byte[field.WireBytes]);
        return parsed;
    }
    public static string Format(object value) => value switch
    {
        PlcDateTime dt => dt.ToString(), PlcTime time => time.ToString(),
        bool b => b ? "true" : "false",
        float f => f.ToString("R", CultureInfo.InvariantCulture),
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        string s => s,
        _ => throw new ArgumentException("Unsupported tag value.")
    };
    public static object Default(PlcType type) => type switch
    {
        PlcType.BOOL => false, PlcType.SINT => (sbyte)0, PlcType.INT => (short)0, PlcType.DINT => 0,
        PlcType.USINT => (byte)0, PlcType.UINT => (ushort)0, PlcType.UDINT => 0U, PlcType.ULINT => 0UL,
        PlcType.DT => new PlcDateTime(0), PlcType.TIME => new PlcTime(0),
        PlcType.LINT => 0L, PlcType.REAL => 0f, PlcType.STRING => "", _ => throw new ArgumentOutOfRangeException(nameof(type))
    };
}
