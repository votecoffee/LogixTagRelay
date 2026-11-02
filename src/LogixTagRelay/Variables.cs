using System.ComponentModel;

namespace LogixTagRelay;

/// <summary>Versioned scalar synchronization state used by explicit and implicit sessions.</summary>
public interface IPlcVariable
{
    /// <summary>Immutable scalar mapping and its PLC access permissions.</summary>
    FieldMap Map { get; }
    /// <summary>Current application-visible value boxed in its matching scalar CLR type.</summary>
    object BoxedValue { get; }
    /// <summary>Last observed PLC value, or null before any observation.</summary>
    object? PlcValue { get; }
    /// <summary>Last synchronization outcome for an application variable.</summary>
    Quality Quality { get; }
    /// <summary>Timestamp of the last accepted value synchronization, or null before success.</summary>
    DateTimeOffset? Timestamp { get; }
    /// <summary>Monotonically increasing application edit version used to preserve concurrent edits.</summary>
    long Version { get; }
    /// <summary>Whether the application edit version has not yet been acknowledged.</summary>
    bool Dirty { get; }
    /// <summary>Atomically captures the application value and edit version for a write.</summary>
    (object Value, long Version) Capture();
    /// <summary>Atomically captures value, edit version and dirty state for a merge or addressed write.</summary>
    (object Value, long Version, bool Dirty) CaptureForMerge();
    /// <summary>Accepts an observed PLC scalar using the configured direction and pending-edit policy.</summary>
    /// <param name="value">Typed scalar or timestamp value required by this operation.</param>
    /// <param name="timestamp">Timestamp of a successful observation or acknowledgement.</param>
    void AcceptRead(object value, DateTimeOffset timestamp);
    /// <summary>Updates PLC readback and refreshes the visible value only when ownership and pending edits permit it.</summary>
    /// <param name="value">Typed scalar or timestamp value required by this operation.</param>
    /// <param name="timestamp">Timestamp of a successful observation or acknowledgement.</param>
    void Observe(object value, DateTimeOffset timestamp);
    /// <summary>Acknowledges only the captured edit version, preserving any later application edit.</summary>
    /// <param name="version">Captured application edit version being acknowledged.</param>
    /// <param name="timestamp">Timestamp of a successful observation or acknowledgement.</param>
    void AcceptWrite(long version, DateTimeOffset timestamp);
    /// <summary>Acknowledges a captured version and adopts the PLC-merged result while preserving later application edits.</summary>
    /// <param name="version">Captured application edit version being acknowledged.</param>
    /// <param name="value">Typed scalar or timestamp value required by this operation.</param>
    /// <param name="timestamp">Timestamp of a successful observation or acknowledgement.</param>
    void AcceptMergedWrite(long version, object value, DateTimeOffset timestamp);
    /// <summary>Marks synchronization quality bad without discarding the current value or pending edits.</summary>
    void MarkBad();
}

/// <summary>Thread-safe typed scalar with versioned edits and change-only notifications. PLC-owned setters are rejected.</summary>
/// <param name="map">Immutable mapping whose PLC type must match the CLR scalar type.</param>
/// <typeparam name="T">CLR value type matching the mapped PLC scalar.</typeparam>
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
    /// <summary>Raised for changed observable properties on the updating thread; identical value reads remain silent.</summary>
    public event PropertyChangedEventHandler? PropertyChanged;
    /// <summary>Raised only when the visible value changes, on the updating thread. Marshal GUI work to its dispatcher.</summary>
    public event EventHandler<PlcValueChangedEventArgs>? ValueChanged;
    /// <summary>Reports isolated subscriber exceptions without changing the communication result.</summary>
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
    /// <summary>Immutable scalar mapping and its PLC access permissions.</summary>
    public FieldMap Map { get; } = map;
    /// <summary>Visible typed value. Assignment marks a writable member dirty; unchanged known values do not raise value events.</summary>
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
    /// <summary>Current application-visible value boxed in its matching scalar CLR type.</summary>
    public object BoxedValue => Value!;
    /// <summary>Last observed PLC value, or null before any observation.</summary>
    public object? PlcValue { get { lock (gate) return plcValue; } }
    /// <summary>Last typed PLC readback; throws InvalidOperationException before an observation exists.</summary>
    public T ObservedValue { get { lock (gate) return plcValue is T observed ? observed : throw new InvalidOperationException("No PLC observation is available yet."); } }
    /// <summary>Whether at least one PLC readback value is available.</summary>
    public bool HasObservation { get { lock (gate) return plcValue is not null; } }
    /// <summary>Timestamp of the most recent PLC observation, including observations that did not change the visible value.</summary>
    public DateTimeOffset? PlcTimestamp { get { lock (gate) return plcTimestamp; } }
    /// <summary>Last synchronization outcome for an application variable.</summary>
    public Quality Quality { get { lock (gate) return quality; } }
    /// <summary>Timestamp of the last accepted value synchronization, or null before success.</summary>
    public DateTimeOffset? Timestamp { get { lock (gate) return timestamp; } }
    /// <summary>Monotonically increasing application edit version used to preserve concurrent edits.</summary>
    public long Version { get { lock (gate) return version; } }
    /// <summary>Whether the application edit version has not yet been acknowledged.</summary>
    public bool Dirty { get { lock (gate) return version != acceptedVersion; } }
    /// <summary>Atomically captures the application value and edit version for a write.</summary>
    public (object Value, long Version) Capture() { lock (gate) return (value!, version); }
    /// <summary>Atomically captures value, edit version and dirty state for a merge or addressed write.</summary>
    public (object Value, long Version, bool Dirty) CaptureForMerge() { lock (gate) return (value!, version, version != acceptedVersion); }
    /// <summary>Accepts an observed PLC scalar using the configured direction and pending-edit policy.</summary>
    /// <param name="newValue">Typed scalar after the transition or observation.</param>
    /// <param name="time">Timestamp assigned to the accepted observation or acknowledgement.</param>
    public void AcceptRead(object newValue, DateTimeOffset time)
    {
        if (Map.Direction == Direction.AppToPlc) throw new InvalidOperationException("Use Observe for command readback.");
        Observe(newValue, time);
    }
    /// <summary>Updates PLC readback and refreshes the visible value only when ownership and pending edits permit it.</summary>
    /// <param name="newValue">Typed scalar after the transition or observation.</param>
    /// <param name="time">Timestamp assigned to the accepted observation or acknowledgement.</param>
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
    /// <summary>Acknowledges only the captured edit version, preserving any later application edit.</summary>
    /// <param name="writtenVersion">Captured application edit version being acknowledged; later edits stay pending.</param>
    /// <param name="time">Timestamp assigned to the accepted observation or acknowledgement.</param>
    public void AcceptWrite(long writtenVersion, DateTimeOffset time)
    {
        State before, after;
        lock (gate) { before = Snapshot(); acceptedVersion = writtenVersion; quality = Quality.Good; timestamp = time; after = Snapshot(); }
        Changed(before, after, ValueChangeOrigin.Plc);
    }
    /// <summary>Marks synchronization quality bad without discarding the current value or pending edits.</summary>
    public void MarkBad()
    {
        State before, after;
        lock (gate) { before = Snapshot(); quality = Quality.Bad; after = Snapshot(); }
        Changed(before, after, ValueChangeOrigin.Plc);
    }
    /// <summary>Acknowledges a captured version and adopts the PLC-merged result while preserving later application edits.</summary>
    /// <param name="writtenVersion">Captured application edit version being acknowledged; later edits stay pending.</param>
    /// <param name="mergedValue">Typed value returned by the PLC-first merge acknowledgement.</param>
    /// <param name="time">Timestamp assigned to the accepted observation or acknowledgement.</param>
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

/// <summary>Ordered scalar members shared by a generated contract and its synchronization session.</summary>
public interface IPlcModel
{
    /// <summary>Fixed ordered membership of the generated contract. Element value changes use property and value events.</summary>
    IReadOnlyList<IPlcVariable> Variables { get; }
}

/// <summary>Checks that a generated model matches its configuration before communication begins.</summary>
public static class ModelValidation
{
    /// <summary>Validates configuration, ordered field identity and scalar CLR representations; rejects mismatched generated peers.</summary>
    /// <param name="config">Matching generated contract configuration.</param>
    /// <param name="model">Live application model instance used by synchronization.</param>
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
