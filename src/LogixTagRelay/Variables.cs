namespace LogixTagRelay;

public interface IPlcVariable
{
    FieldMap Map { get; }
    object BoxedValue { get; }
    object? PlcValue { get; }
    Quality Quality { get; }
    DateTimeOffset? Timestamp { get; }
    long Version { get; }
    bool Dirty { get; }
    (object Value, long Version) Capture();
    void AcceptRead(object value, DateTimeOffset timestamp);
    void Observe(object value, DateTimeOffset timestamp);
    void AcceptWrite(long version, DateTimeOffset timestamp);
    void MarkBad();
}

public sealed class PlcVariable<T>(FieldMap map) : IPlcVariable
{
    private readonly object gate = new();
    private T value = typeof(T) == typeof(string) ? (T)(object)string.Empty : default!;
    private object? plcValue;
    private DateTimeOffset? plcTimestamp;
    private long version;
    private long acceptedVersion;
    private Quality quality;
    private DateTimeOffset? timestamp;
    public FieldMap Map { get; } = map;
    public T Value
    {
        get { lock (gate) return value; }
        set
        {
            if (Map.Direction != Direction.AppToPlc) throw new InvalidOperationException("PLC owns this variable.");
            if (Map.Type == PlcType.STRING) PlcTypes.ValidateString((string)(object)value!, Map.StringCapacity);
            lock (gate) { this.value = value; version++; }
        }
    }
    public object BoxedValue => Value!;
    public object? PlcValue { get { lock (gate) return plcValue; } }
    public T ObservedValue { get { lock (gate) return plcValue is T observed ? observed : throw new InvalidOperationException("No PLC observation is available yet."); } }
    public bool HasObservation { get { lock (gate) return plcValue is not null; } }
    public DateTimeOffset? PlcTimestamp { get { lock (gate) return plcTimestamp; } }
    public Quality Quality { get { lock (gate) return quality; } }
    public DateTimeOffset? Timestamp { get { lock (gate) return timestamp; } }
    public long Version { get { lock (gate) return version; } }
    public bool Dirty { get { lock (gate) return version != acceptedVersion; } }
    public (object Value, long Version) Capture() { lock (gate) return (value!, version); }
    public void AcceptRead(object newValue, DateTimeOffset time)
    {
        if (Map.Direction != Direction.PlcToApp) throw new InvalidOperationException("App owns this variable.");
        Observe(newValue, time);
    }
    public void Observe(object newValue, DateTimeOffset time)
    {
        if (Map.Type == PlcType.STRING) PlcTypes.ValidateString((string)newValue, Map.StringCapacity);
        lock (gate)
        {
            plcValue = (T)newValue;
            plcTimestamp = time;
            if (Map.Direction == Direction.PlcToApp) { value = (T)newValue; quality = Quality.Good; timestamp = time; }
        }
    }
    public void AcceptWrite(long writtenVersion, DateTimeOffset time)
    {
        lock (gate) { acceptedVersion = writtenVersion; quality = Quality.Good; timestamp = time; }
    }
    public void MarkBad() { lock (gate) quality = Quality.Bad; }
}

public interface IPlcModel { IReadOnlyList<IPlcVariable> Variables { get; } }

public static class ModelValidation
{
    public static void Validate(SyncConfiguration config, IPlcModel model)
    {
        config.Validate();
        if (model.Variables.Count != config.Fields.Count ||
            !model.Variables.Select(v => v.Map).SequenceEqual(config.Fields))
            throw new ArgumentException("Generated model does not match the configuration. Regenerate both together.");
        foreach (var variable in model.Variables)
        {
            var probe = new byte[variable.Map.WireBytes];
            PlcTypes.Encode(variable.Map, variable.BoxedValue, probe);
        }
    }
}
