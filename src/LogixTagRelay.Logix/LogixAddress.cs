using System.Text.RegularExpressions;

namespace LogixTagRelay.Logix;

public static class LogixAddress
{
    public static string SymbolicPath(FieldMap field)
    {
        if (field.Type != PlcType.BOOL || !field.Path.EndsWith(']')) return field.Path;
        var m = Regex.Match(field.Path, @"^(.*)\[(\d+)\]$");
        if (!m.Success) throw new NotSupportedException("The explicit driver requires one-dimensional BOOL arrays for bit addressing.");
        int index = int.Parse(m.Groups[2].Value);
        return $"{m.Groups[1].Value}[{index / 32}].{index % 32}";
    }
}
