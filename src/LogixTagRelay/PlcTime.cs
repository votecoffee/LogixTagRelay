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

/// <summary>Logix TIME32: signed 32-bit duration in microseconds.</summary>
public readonly record struct PlcTime32(int Microseconds)
{
    public TimeSpan ToTimeSpan() => TimeSpan.FromTicks((long)Microseconds * 10);
    public static PlcTime32 FromTimeSpan(TimeSpan value) => new(checked((int)PlcTime.FromTimeSpan(value).Microseconds));
    public override string ToString() => Microseconds.ToString(CultureInfo.InvariantCulture);
}
/// <summary>Logix LTIME: exact signed 64-bit duration in nanoseconds.</summary>
public readonly record struct PlcLongTime(long Nanoseconds)
{
    public TimeSpan ToTimeSpan()
    {
        if (Nanoseconds % 100 != 0) throw new InvalidOperationException("Sub-tick nanoseconds cannot be represented by TimeSpan.");
        return TimeSpan.FromTicks(Nanoseconds / 100);
    }
    public static PlcLongTime FromTimeSpan(TimeSpan value) => new(checked(value.Ticks * 100));
    public override string ToString() => Nanoseconds.ToString(CultureInfo.InvariantCulture);
}
/// <summary>Logix LDT: exact signed 64-bit nanoseconds since Unix epoch UTC.</summary>
public readonly record struct PlcLongDateTime(long UnixNanoseconds)
{
    public DateTimeOffset ToDateTimeOffset()
    {
        if (UnixNanoseconds % 100 != 0) throw new InvalidOperationException("Sub-tick nanoseconds cannot be represented by DateTimeOffset.");
        return DateTimeOffset.UnixEpoch.AddTicks(UnixNanoseconds / 100);
    }
    public static PlcLongDateTime FromDateTimeOffset(DateTimeOffset value) => new(checked((value - DateTimeOffset.UnixEpoch).Ticks * 100));
    public override string ToString() => UnixNanoseconds.ToString(CultureInfo.InvariantCulture);
}
