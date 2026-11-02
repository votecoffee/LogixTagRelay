using System.Text.RegularExpressions;

namespace LogixTagRelay.Logix;

/// <summary>Converts relay scalar mappings to native Logix symbolic addresses.</summary>
public static class LogixAddress
{
    /// <summary>Returns the native address, translating one-dimensional BOOL array elements to packed DINT bit addresses.</summary>
    /// <param name="field">Scalar mapping, including STRING capacity when applicable.</param>
    public static string SymbolicPath(FieldMap field)
    {
        if (field.Type != PlcType.BOOL || !field.Path.EndsWith(']')) return field.Path;
        var m = Regex.Match(field.Path, @"^(.*)\[(\d+)\]$");
        if (!m.Success) throw new NotSupportedException("The explicit driver requires one-dimensional BOOL arrays for bit addressing.");
        int index = int.Parse(m.Groups[2].Value);
        return $"{m.Groups[1].Value}[{index / 32}].{index % 32}";
    }
}
