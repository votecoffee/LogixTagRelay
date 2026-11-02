namespace LogixTagRelay;

public interface IPlcVariable
{
    FieldMap Map { get; }
    object BoxedValue { get; }
    Quality Quality { get; }
    DateTimeOffset? Timestamp { get; }
    long Version { get; }
    bool Dirty { get; }
    (object Value, long Version) Capture();
    void AcceptRead(object value, DateTimeOffset timestamp);
    void AcceptWrite(long version, DateTimeOffset timestamp);
    void MarkBad();
}

public sealed class PlcVariable<T>(FieldMap map) : IPlcVariable where T : struct
{
    private readonly object gate = new();
    private T value;
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
            lock (gate) { this.value = value; version++; }
        }
    }
    public object BoxedValue => Value;
    public Quality Quality { get { lock (gate) return quality; } }
    public DateTimeOffset? Timestamp { get { lock (gate) return timestamp; } }
    public long Version { get { lock (gate) return version; } }
    public bool Dirty { get { lock (gate) return version != acceptedVersion; } }
    public (object Value, long Version) Capture() { lock (gate) return (value, version); }
    public void AcceptRead(object newValue, DateTimeOffset time)
    {
        if (Map.Direction != Direction.PlcToApp) throw new InvalidOperationException("App owns this variable.");
        lock (gate) { value = (T)newValue; quality = Quality.Good; timestamp = time; }
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
            var probe = new byte[8];
            PlcTypes.Encode(variable.Map.Type, variable.BoxedValue, probe);
        }
    }
}
