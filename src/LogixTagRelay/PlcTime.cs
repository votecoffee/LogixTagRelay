using System.Globalization;

namespace LogixTagRelay;

/// <summary>Logix DT: exact signed 64-bit microseconds since 1970-01-01 UTC.</summary>
/// <param name="UnixMicroseconds">Signed microseconds since 1970-01-01 UTC; the raw PLC value is retained exactly.</param>
public readonly record struct PlcDateTime(long UnixMicroseconds)
{
    /// <summary>Converts exactly to UTC DateTimeOffset; throws when the value is out of range or loses sub-tick precision.</summary>
    public DateTimeOffset ToDateTimeOffset() => DateTimeOffset.UnixEpoch.AddTicks(checked(UnixMicroseconds * 10));
    /// <summary>Converts a timestamp to the PLC&apos;s exact epoch units, rejecting precision loss and overflow.</summary>
    /// <param name="value">Typed scalar or timestamp value required by this operation.</param>
    public static PlcDateTime FromDateTimeOffset(DateTimeOffset value)
    {
        long ticks = (value - DateTimeOffset.UnixEpoch).Ticks;
        if (ticks % 10 != 0) throw new ArgumentException("DT supports whole microseconds; sub-microsecond ticks would lose precision.", nameof(value));
        return new(ticks / 10);
    }
    /// <summary>Returns the exact raw integer value in invariant culture.</summary>
    public override string ToString() => UnixMicroseconds.ToString(CultureInfo.InvariantCulture);
}

/// <summary>Logix TIME: exact signed 64-bit duration in microseconds.</summary>
/// <param name="Microseconds">Signed duration in whole microseconds.</param>
public readonly record struct PlcTime(long Microseconds)
{
    /// <summary>Converts exactly to TimeSpan; throws for overflow or unrepresentable sub-tick nanoseconds.</summary>
    public TimeSpan ToTimeSpan() => TimeSpan.FromTicks(checked(Microseconds * 10));
    /// <summary>Converts a duration to the PLC&apos;s exact units, rejecting precision loss and overflow.</summary>
    /// <param name="value">Typed scalar or timestamp value required by this operation.</param>
    public static PlcTime FromTimeSpan(TimeSpan value)
    {
        if (value.Ticks % 10 != 0) throw new ArgumentException("TIME supports whole microseconds; sub-microsecond ticks would lose precision.", nameof(value));
        return new(value.Ticks / 10);
    }
    /// <summary>Returns the exact raw integer value in invariant culture.</summary>
    public override string ToString() => Microseconds.ToString(CultureInfo.InvariantCulture);
}

/// <summary>Logix TIME32: signed 32-bit duration in microseconds.</summary>
/// <param name="Microseconds">Signed duration in whole microseconds.</param>
public readonly record struct PlcTime32(int Microseconds)
{
    /// <summary>Converts exactly to TimeSpan; throws for overflow or unrepresentable sub-tick nanoseconds.</summary>
    public TimeSpan ToTimeSpan() => TimeSpan.FromTicks((long)Microseconds * 10);
    /// <summary>Converts a duration to the PLC&apos;s exact units, rejecting precision loss and overflow.</summary>
    /// <param name="value">Typed scalar or timestamp value required by this operation.</param>
    public static PlcTime32 FromTimeSpan(TimeSpan value) => new(checked((int)PlcTime.FromTimeSpan(value).Microseconds));
    /// <summary>Returns the exact raw integer value in invariant culture.</summary>
    public override string ToString() => Microseconds.ToString(CultureInfo.InvariantCulture);
}
/// <summary>Logix LTIME: exact signed 64-bit duration in nanoseconds.</summary>
/// <param name="Nanoseconds">Signed duration in nanoseconds; conversions reject sub-tick precision loss.</param>
public readonly record struct PlcLongTime(long Nanoseconds)
{
    /// <summary>Converts exactly to TimeSpan; throws for overflow or unrepresentable sub-tick nanoseconds.</summary>
    public TimeSpan ToTimeSpan()
    {
        if (Nanoseconds % 100 != 0) throw new InvalidOperationException("Sub-tick nanoseconds cannot be represented by TimeSpan.");
        return TimeSpan.FromTicks(Nanoseconds / 100);
    }
    /// <summary>Converts a duration to the PLC&apos;s exact units, rejecting precision loss and overflow.</summary>
    /// <param name="value">Typed scalar or timestamp value required by this operation.</param>
    public static PlcLongTime FromTimeSpan(TimeSpan value) => new(checked(value.Ticks * 100));
    /// <summary>Returns the exact raw integer value in invariant culture.</summary>
    public override string ToString() => Nanoseconds.ToString(CultureInfo.InvariantCulture);
}
/// <summary>Logix LDT: exact signed 64-bit nanoseconds since Unix epoch UTC.</summary>
/// <param name="UnixNanoseconds">Signed nanoseconds since the Unix epoch; conversions reject sub-tick precision loss.</param>
public readonly record struct PlcLongDateTime(long UnixNanoseconds)
{
    /// <summary>Converts exactly to UTC DateTimeOffset; throws when the value is out of range or loses sub-tick precision.</summary>
    public DateTimeOffset ToDateTimeOffset()
    {
        if (UnixNanoseconds % 100 != 0) throw new InvalidOperationException("Sub-tick nanoseconds cannot be represented by DateTimeOffset.");
        return DateTimeOffset.UnixEpoch.AddTicks(UnixNanoseconds / 100);
    }
    /// <summary>Converts a timestamp to the PLC&apos;s exact epoch units, rejecting precision loss and overflow.</summary>
    /// <param name="value">Typed scalar or timestamp value required by this operation.</param>
    public static PlcLongDateTime FromDateTimeOffset(DateTimeOffset value) => new(checked((value - DateTimeOffset.UnixEpoch).Ticks * 100));
    /// <summary>Returns the exact raw integer value in invariant culture.</summary>
    public override string ToString() => UnixNanoseconds.ToString(CultureInfo.InvariantCulture);
}
