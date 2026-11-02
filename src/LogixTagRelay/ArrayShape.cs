using System.Globalization;

namespace LogixTagRelay;

/// <summary>Checked row-major indexing for one-, two- and three-dimensional Logix arrays.</summary>
public static class ArrayShape
{
    /// <summary>Returns the checked product of positive array dimensions.</summary>
    /// <param name="dimensions">Positive array dimensions in native order, at most three dimensions.</param>
    public static int Count(IReadOnlyList<int> dimensions)
    {
        if (dimensions.Count is < 1 or > 3 || dimensions.Any(d => d < 1))
            throw new ArgumentException("Logix arrays require 1..3 positive dimensions.");
        long count = 1;
        foreach (int dimension in dimensions) { count *= dimension; if (count > 20000) throw new ArgumentException("Array expansion exceeds 20000 elements."); }
        return (int)count;
    }
    /// <summary>Converts a zero-based row-major linear index to coordinates, with the last dimension varying fastest.</summary>
    /// <param name="linearIndex">Zero-based row-major element index.</param>
    /// <param name="dimensions">Positive array dimensions in native order, at most three dimensions.</param>
    public static int[] Coordinates(int linearIndex, IReadOnlyList<int> dimensions)
    {
        if ((uint)linearIndex >= (uint)Count(dimensions)) throw new ArgumentOutOfRangeException(nameof(linearIndex));
        var result = new int[dimensions.Count];
        for (int i = dimensions.Count - 1; i >= 0; i--) { result[i] = linearIndex % dimensions[i]; linearIndex /= dimensions[i]; }
        return result;
    }
    /// <summary>Formats row-major coordinates as a Logix array subscript, such as [1,2].</summary>
    /// <param name="linearIndex">Zero-based row-major element index.</param>
    /// <param name="dimensions">Positive array dimensions in native order, at most three dimensions.</param>
    public static string Subscript(int linearIndex, IReadOnlyList<int> dimensions) =>
        string.Join(",", Coordinates(linearIndex, dimensions).Select(i => i.ToString(CultureInfo.InvariantCulture)));
}
