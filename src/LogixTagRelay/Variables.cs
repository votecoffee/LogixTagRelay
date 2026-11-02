using System.ComponentModel;

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
    (object Value, long Version, bool Dirty) CaptureForMerge();
    void AcceptRead(object value, DateTimeOffset timestamp);
    void Observe(object value, DateTimeOffset timestamp);
    void AcceptWrite(long version, DateTimeOffset timestamp);
    void AcceptMergedWrite(long version, object value, DateTimeOffset timestamp);
    void MarkBad();
}

public sealed class PlcVariable<T>(FieldMap map) : IObservablePlcVariable
{
    private readonly object gate = new();
    private T value = typeof(T) == typeof(string) ? (T)(object)string.Empty : default!;
    private object? plcValue;
    private DateTimeOffset? plcTimestamp;
    private long version;
    private long acceptedVersion;
    private Quality quality;
    private DateTimeOffset? timestamp;
    public event PropertyChangedEventHandler? PropertyChanged;
    public event EventHandler<PlcValueChangedEventArgs>? ValueChanged;
    public event EventHandler<NotificationFailedEventArgs>? NotificationFailed;
    private readonly record struct State(T Value, object? PlcValue, bool Dirty, Quality Quality, long Version);
    private State Snapshot() => new(value, plcValue, version != acceptedVersion, quality, version);
    private void Changed(State before, State after, ValueChangeOrigin origin)
    {
        if (before.Equals(after)) return;
        void Property(string name) => Notifications.Raise(PropertyChanged,
            h => ((PropertyChangedEventHandler)h)(this, new(name)), this, NotificationFailed);
        if (!EqualityComparer<T>.Default.Equals(before.Value, after.Value))
        {
            Property(nameof(Value)); Property(nameof(BoxedValue));
            var args = new PlcValueChangedEventArgs(this, before.Value!, after.Value!, origin);
            Notifications.Raise(ValueChanged, h => ((EventHandler<PlcValueChangedEventArgs>)h)(this, args), this, NotificationFailed);
        }
        if (!Equals(before.PlcValue, after.PlcValue)) { Property(nameof(PlcValue)); Property(nameof(ObservedValue)); }
        if ((before.PlcValue is null) != (after.PlcValue is null)) Property(nameof(HasObservation));
        if (before.Dirty != after.Dirty) Property(nameof(Dirty));
        if (before.Quality != after.Quality) Property(nameof(Quality));
        if (before.Version != after.Version) Property(nameof(Version));
        // Timestamps remain queryable. Polling the same value does not emit notifications.
    }
    public FieldMap Map { get; } = map;
    public T Value
    {
        get { lock (gate) return value; }
        set
        {
            if (!Map.CanWrite) throw new InvalidOperationException("This contract member is read-only to the application.");
            if (Map.Type == PlcType.STRING) PlcTypes.ValidateString((string)(object)value!, Map.StringCapacity);
            State before, after;
            lock (gate)
            {
                before = Snapshot();
                // First assignment is intentional even when it equals an unknown CLR default.
                bool reassertCommand = Map.Direction == Direction.AppToPlc && version == acceptedVersion && plcValue is T observed && !EqualityComparer<T>.Default.Equals(observed, value);
                if (EqualityComparer<T>.Default.Equals(this.value, value) && (version != 0 || plcValue is not null) && !reassertCommand) return;
                this.value = value; version++;
                after = Snapshot();
            }
            Changed(before, after, ValueChangeOrigin.Application);
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
    public (object Value, long Version, bool Dirty) CaptureForMerge() { lock (gate) return (value!, version, version != acceptedVersion); }
    public void AcceptRead(object newValue, DateTimeOffset time)
    {
        if (Map.Direction == Direction.AppToPlc) throw new InvalidOperationException("Use Observe for command readback.");
        Observe(newValue, time);
    }
    public void Observe(object newValue, DateTimeOffset time)
    {
        if (Map.Type == PlcType.STRING) PlcTypes.ValidateString((string)newValue, Map.StringCapacity);
        State before, after;
        lock (gate)
        {
            before = Snapshot();
            plcValue = (T)newValue;
            plcTimestamp = time;
            if (Map.Direction == Direction.PlcToApp || (Map.Direction == Direction.Bidirectional && version == acceptedVersion))
            { value = (T)newValue; quality = Quality.Good; timestamp = time; }
            after = Snapshot();
        }
        Changed(before, after, ValueChangeOrigin.Plc);
    }
    public void AcceptWrite(long writtenVersion, DateTimeOffset time)
    {
        State before, after;
        lock (gate) { before = Snapshot(); acceptedVersion = writtenVersion; quality = Quality.Good; timestamp = time; after = Snapshot(); }
        Changed(before, after, ValueChangeOrigin.Plc);
    }
    public void MarkBad()
    {
        State before, after;
        lock (gate) { before = Snapshot(); quality = Quality.Bad; after = Snapshot(); }
        Changed(before, after, ValueChangeOrigin.Plc);
    }
    public void AcceptMergedWrite(long writtenVersion, object mergedValue, DateTimeOffset time)
    {
        if (Map.Type == PlcType.STRING) PlcTypes.ValidateString((string)mergedValue, Map.StringCapacity);
        State before, after;
        lock (gate)
        {
            before = Snapshot();
            // PLC may win byte conflicts. Preserve edits made after this request was captured.
            if (version == writtenVersion) value = (T)mergedValue;
            acceptedVersion = writtenVersion;
            plcValue = (T)mergedValue; plcTimestamp = time;
            quality = Quality.Good; timestamp = time;
            after = Snapshot();
        }
        Changed(before, after, ValueChangeOrigin.Plc);
    }
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
