using System.Globalization;

namespace LogixTagRelay;

/// <summary>Logix DT: exact signed 64-bit microseconds since 1970-01-01 UTC.</summary>
public readonly record struct PlcDateTime(long UnixMicroseconds)
{
    public DateTimeOffset ToDateTimeOffset() => DateTimeOffset.UnixEpoch.AddTicks(checked(UnixMicroseconds * 10));
    public static PlcDateTime FromDateTimeOffset(DateTimeOffset value)
    {
        long ticks = (value - DateTimeOffset.UnixEpoch).Ticks;
        if (ticks % 10 != 0) throw new ArgumentException("DT supports whole microseconds; sub-microsecond ticks would lose precision.", nameof(value));
        return new(ticks / 10);
    }
    public override string ToString() => UnixMicroseconds.ToString(CultureInfo.InvariantCulture);
}

/// <summary>Logix TIME: exact signed 64-bit duration in microseconds.</summary>
public readonly record struct PlcTime(long Microseconds)
{
    public TimeSpan ToTimeSpan() => TimeSpan.FromTicks(checked(Microseconds * 10));
    public static PlcTime FromTimeSpan(TimeSpan value)
    {
        if (value.Ticks % 10 != 0) throw new ArgumentException("TIME supports whole microseconds; sub-microsecond ticks would lose precision.", nameof(value));
        return new(value.Ticks / 10);
    }
    public override string ToString() => Microseconds.ToString(CultureInfo.InvariantCulture);
}
