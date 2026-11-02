using System.ComponentModel;

namespace LogixTagRelay;

public enum SessionState { Stopped, Connecting, AwaitingBaseline, Running, Recovering, Faulted }
public enum SessionKind { Explicit, Implicit }
public sealed record TriggerStatus(int Request, bool Succeeded, bool NotificationsDelivered, string? Error);
public sealed record SessionSnapshot(SessionState State, long SuccessfulUpdates, long Failures, long Recoveries,
    long MissedDeadlines, DateTimeOffset? LastCommunication, DateTimeOffset? LastSuccessfulUpdate,
    TimeSpan? DataAge, TimeSpan LastCycleDuration, TimeSpan MaximumCycleDuration, string? LastError,
    bool WritesSuspended, TriggerStatus? LastTrigger);

public sealed class SessionDiagnostics : INotifyPropertyChanged
{
    private readonly object gate = new();
    private SessionSnapshot state = new(SessionState.Stopped, 0, 0, 0, 0, null, null, null, TimeSpan.Zero, TimeSpan.Zero, null, false, null);
    internal Exception? LastException { get; private set; }
    public SessionSnapshot Snapshot { get { lock (gate) return state with { DataAge = state.LastSuccessfulUpdate is { } t ? DateTimeOffset.UtcNow - t : null }; } }
    public event PropertyChangedEventHandler? PropertyChanged;
    public event EventHandler<SessionSnapshot>? Changed;
    public event EventHandler<NotificationFailedEventArgs>? NotificationFailed;
    private void Update(Func<SessionSnapshot, SessionSnapshot> change)
    {
        SessionSnapshot snapshot;
        lock (gate) { state = change(state); snapshot = state; }
        Notifications.Raise(PropertyChanged, h => ((PropertyChangedEventHandler)h)(this, new(nameof(Snapshot))), this, NotificationFailed);
        Notifications.Raise(Changed, h => ((EventHandler<SessionSnapshot>)h)(this, snapshot), this, NotificationFailed);
    }
    internal void SetState(SessionState value) => Update(s => s with { State = value });
    internal void BeginAttempt() { LastException = null; SetState(SessionState.Connecting); }
    internal void Contact() => Update(s => s with { LastCommunication = DateTimeOffset.UtcNow });
    internal void Success(TimeSpan duration, int deadlineMs) => Update(s => s with
    {
        State = SessionState.Running, SuccessfulUpdates = s.SuccessfulUpdates + 1,
        LastCommunication = DateTimeOffset.UtcNow, LastSuccessfulUpdate = DateTimeOffset.UtcNow,
        LastCycleDuration = duration, MaximumCycleDuration = duration > s.MaximumCycleDuration ? duration : s.MaximumCycleDuration,
        MissedDeadlines = s.MissedDeadlines + (duration.TotalMilliseconds > deadlineMs ? 1 : 0)
    });
    internal void Failure(Exception error)
    {
        if (ReferenceEquals(LastException, error)) return;
        LastException = error; Update(s => s with { State = SessionState.Faulted, Failures = s.Failures + 1, LastError = error.Message });
    }
    internal void Recover(bool suspended) => Update(s => s with { State = SessionState.Recovering, Recoveries = s.Recoveries + 1, WritesSuspended = suspended });
    internal void Suspend(bool suspended) => Update(s => s with { WritesSuspended = suspended });
    internal void Trigger(TriggerResult result) => Update(s => s with { LastTrigger = new(result.Request, result.Succeeded, result.NotificationsDelivered, result.Error?.Message) });
}

public enum PendingWritePolicy { Suspend, Retry }
public sealed record RecoveryOptions(int InitialDelayMs = 250, int MaximumDelayMs = 5000, int MaximumAttempts = 0,
    int ConnectionTimeoutMs = 10000, int StaleTimeoutMs = 1000, PendingWritePolicy PendingWrites = PendingWritePolicy.Suspend)
{
    public void Validate()
    {
        if (InitialDelayMs < 1 || MaximumDelayMs < InitialDelayMs || MaximumAttempts < 0 || ConnectionTimeoutMs < 1 || StaleTimeoutMs < 1 || !Enum.IsDefined(PendingWrites))
            throw new ArgumentOutOfRangeException(nameof(RecoveryOptions));
    }
    internal int Delay(int attempt) => (int)Math.Min(MaximumDelayMs, InitialDelayMs * Math.Pow(2, Math.Min(attempt - 1, 20)));
    internal bool Retry(Exception error, int attempt) => (MaximumAttempts == 0 || attempt <= MaximumAttempts) &&
        (error is IOException or TimeoutException || error.GetType().FullName == "libplctag.LibPlcTagException");
}

public interface IRelaySession
{
    SessionKind Kind { get; }
    SyncConfiguration Configuration { get; }
    IPlcModel Model { get; }
    SessionDiagnostics Diagnostics { get; }
    void ResumeWrites();
    Task RunAsync(CancellationToken cancellationToken);
}
