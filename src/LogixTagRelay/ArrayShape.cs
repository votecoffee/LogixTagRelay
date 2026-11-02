using System.Globalization;

namespace LogixTagRelay;

public static class ArrayShape
{
    public static int Count(IReadOnlyList<int> dimensions)
    {
        if (dimensions.Count is < 1 or > 3 || dimensions.Any(d => d < 1))
            throw new ArgumentException("Logix arrays require 1..3 positive dimensions.");
        long count = 1;
        foreach (int dimension in dimensions) { count *= dimension; if (count > 20000) throw new ArgumentException("Array expansion exceeds 20000 elements."); }
        return (int)count;
    }
    public static int[] Coordinates(int linearIndex, IReadOnlyList<int> dimensions)
    {
        if ((uint)linearIndex >= (uint)Count(dimensions)) throw new ArgumentOutOfRangeException(nameof(linearIndex));
        var result = new int[dimensions.Count];
        for (int i = dimensions.Count - 1; i >= 0; i--) { result[i] = linearIndex % dimensions[i]; linearIndex /= dimensions[i]; }
        return result;
    }
    public static string Subscript(int linearIndex, IReadOnlyList<int> dimensions) =>
        string.Join(",", Coordinates(linearIndex, dimensions).Select(i => i.ToString(CultureInfo.InvariantCulture)));
}
