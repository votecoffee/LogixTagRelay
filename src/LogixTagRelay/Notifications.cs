using System.Collections.ObjectModel;
using System.ComponentModel;

namespace LogixTagRelay;

public enum ValueChangeOrigin { Application, Plc }

public sealed class PlcValueChangedEventArgs(IPlcVariable variable, object oldValue, object newValue, ValueChangeOrigin origin) : EventArgs
{
    public IPlcVariable Variable { get; } = variable;
    public string Path => Variable.Map.Path;
    public object OldValue { get; } = oldValue;
    public object NewValue { get; } = newValue;
    public ValueChangeOrigin Origin { get; } = origin;
}

public sealed class NotificationFailedEventArgs(Exception exception) : EventArgs
{
    public Exception Exception { get; } = exception;
}

public interface IObservablePlcVariable : IPlcVariable, INotifyPropertyChanged
{
    event EventHandler<PlcValueChangedEventArgs>? ValueChanged;
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
    public ReadOnlyObservableCollection<IPlcVariable> Variables => variables ?? throw new InvalidOperationException("Contract has not been initialized.");
    IReadOnlyList<IPlcVariable> IPlcModel.Variables => Variables;
    public event PropertyChangedEventHandler? PropertyChanged;
    public event EventHandler<PlcValueChangedEventArgs>? TagValueChanged;
    public event EventHandler<NotificationFailedEventArgs>? NotificationFailed;

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
