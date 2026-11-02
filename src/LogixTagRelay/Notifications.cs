using System.Collections.ObjectModel;
using System.ComponentModel;

namespace LogixTagRelay;

/// <summary>Source of a value transition delivered to application observers.</summary>
public enum ValueChangeOrigin
{
    /// <summary>An application assignment changed the desired value.</summary>
    Application,
    /// <summary>A PLC observation or merged acknowledgement changed the value.</summary>
    Plc,
}

/// <summary>One actual scalar value transition; identical reads and acknowledgements do not raise a value event.</summary>
/// <param name="variable">Variable whose visible scalar value changed.</param>
/// <param name="oldValue">Typed scalar before the transition.</param>
/// <param name="newValue">Typed scalar after the transition or observation.</param>
/// <param name="origin">Whether the value change originated in the application or PLC.</param>
public sealed class PlcValueChangedEventArgs(IPlcVariable variable, object oldValue, object newValue, ValueChangeOrigin origin) : EventArgs
{
    /// <summary>Variable whose value changed.</summary>
    public IPlcVariable Variable { get; } = variable;
    /// <summary>Fully qualified Logix scalar path, including program scope and array coordinates when applicable.</summary>
    public string Path => Variable.Map.Path;
    /// <summary>Typed scalar value before the transition.</summary>
    public object OldValue { get; } = oldValue;
    /// <summary>Typed scalar value after the transition.</summary>
    public object NewValue { get; } = newValue;
    /// <summary>Whether the transition came from an application edit or PLC update.</summary>
    public ValueChangeOrigin Origin { get; } = origin;
}

/// <summary>Failure thrown by an observer and isolated from communication processing.</summary>
/// <param name="exception">Original event-subscriber exception.</param>
public sealed class NotificationFailedEventArgs(Exception exception) : EventArgs
{
    /// <summary>Original exception thrown by an event subscriber.</summary>
    public Exception Exception { get; } = exception;
}

/// <summary>Scalar value with property and value-transition notifications on the updating thread.</summary>
public interface IObservablePlcVariable : IPlcVariable, INotifyPropertyChanged
{
    /// <summary>Raised only when the visible value changes, on the updating thread. Marshal GUI work to its dispatcher.</summary>
    event EventHandler<PlcValueChangedEventArgs>? ValueChanged;
    /// <summary>Reports isolated subscriber exceptions without changing the communication result.</summary>
    event EventHandler<NotificationFailedEventArgs>? NotificationFailed;
}

// Notification failures must not turn a completed PLC write into a communication failure.
internal static class Notifications
{
    public static void Raise(Delegate? handlers, Action<Delegate> invoke, object sender, EventHandler<NotificationFailedEventArgs>? failed)
    {
        if (handlers is null) return;
        foreach (var handler in handlers.GetInvocationList())
        {
            try { invoke(handler); }
            catch (Exception error)
            {
                System.Diagnostics.Trace.TraceError("PLC notification handler failed: {0}", error);
                if (failed is null) continue;
                foreach (EventHandler<NotificationFailedEventArgs> report in failed.GetInvocationList())
                    try { report(sender, new(error)); }
                    catch (Exception reportError) { System.Diagnostics.Trace.TraceError("Notification failure handler failed: {0}", reportError); }
            }
        }
    }
}

/// <summary>Stable generated contract with per-tag value events and standard property notifications.</summary>
public abstract class PlcModel : IPlcModel, INotifyPropertyChanged
{
    private ReadOnlyObservableCollection<IPlcVariable>? variables;
    /// <summary>Fixed ordered membership of the generated contract. Element value changes use property and value events.</summary>
    public ReadOnlyObservableCollection<IPlcVariable> Variables => variables ?? throw new InvalidOperationException("Contract has not been initialized.");
    IReadOnlyList<IPlcVariable> IPlcModel.Variables => Variables;
    /// <summary>Raised for changed observable properties on the updating thread; identical value reads remain silent.</summary>
    public event PropertyChangedEventHandler? PropertyChanged;
    /// <summary>Raised for an actual member value transition with its PLC path, typed values and origin.</summary>
    public event EventHandler<PlcValueChangedEventArgs>? TagValueChanged;
    /// <summary>Reports isolated subscriber exceptions without changing the communication result.</summary>
    public event EventHandler<NotificationFailedEventArgs>? NotificationFailed;

    /// <summary>Initializes fixed membership exactly once and connects member notifications to generated top-level properties.</summary>
    /// <param name="members">Fixed ordered variables making up the generated contract.</param>
    /// <param name="propertyNames">Generated top-level property name corresponding to each member.</param>
    protected void InitializeVariables(IPlcVariable[] members, string[] propertyNames)
    {
        if (variables is not null || members.Length != propertyNames.Length) throw new InvalidOperationException("Initialize a contract exactly once with matching property names.");
        variables = new(new ObservableCollection<IPlcVariable>(members));
        for (int i = 0; i < members.Length; i++)
        {
            if (members[i] is not IObservablePlcVariable observable) continue;
            string property = propertyNames[i];
            observable.ValueChanged += (_, change) =>
            {
                Notifications.Raise(PropertyChanged, h => ((PropertyChangedEventHandler)h)(this, new(property)), this, NotificationFailed);
                Notifications.Raise(TagValueChanged, h => ((EventHandler<PlcValueChangedEventArgs>)h)(this, change), this, NotificationFailed);
            };
            observable.NotificationFailed += (_, failure) => Notifications.Raise(NotificationFailed,
                h => ((EventHandler<NotificationFailedEventArgs>)h)(this, failure), this, null);
        }
    }
}
