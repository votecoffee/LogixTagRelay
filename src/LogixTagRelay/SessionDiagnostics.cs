using System.ComponentModel;

namespace LogixTagRelay;

public enum SessionState { Stopped, Connecting, AwaitingBaseline, Running, Recovering, Faulted }
public enum SessionKind { Explicit, Implicit }
public sealed record TriggerStatus(int Request, bool Succeeded, bool NotificationsDelivered, string? Error);
public sealed record SessionSnapshot(SessionState State, long SuccessfulUpdates, long Failures, long Recoveries,
    long MissedDeadlines, DateTimeOffset? LastCommunication, DateTimeOffset? LastSuccessfulUpdate,
    TimeSpan? DataAge, TimeSpan LastCycleDuration, TimeSpan MaximumCycleDuration, string? LastError,
    bool WritesSuspended, TriggerStatus? LastTrigger)
{
    public PlcFault? LastFailure { get; init; }
    public long MissedIntervals { get; init; }
    public long OverlappingRequests { get; init; }
    public TimeSpan LastStartLateness { get; init; }
    public TimeSpan MaximumStartLateness { get; init; }
    public int LastRetryDelayMs { get; init; }
    public DateTimeOffset? NextRetryAt { get; init; }
    public long HeartbeatTransitions { get; init; }
    public bool? HeartbeatValue { get; init; }
    public DateTimeOffset? LastHeartbeat { get; init; }
    public long HeartbeatMissedIntervals { get; init; }
    public SessionState? HeartbeatState { get; init; }
    public PlcFault? HeartbeatFailure { get; init; }
}

public sealed class SessionDiagnostics : INotifyPropertyChanged
{
    private readonly object gate = new();
    private SessionSnapshot state = new(SessionState.Stopped, 0, 0, 0, 0, null, null, null, TimeSpan.Zero, TimeSpan.Zero, null, false, null);
    private Exception? lastException, lastReported;
    public Exception? LastException => Volatile.Read(ref lastException);
    public SessionSnapshot Snapshot { get { lock (gate) return state with { DataAge = state.LastSuccessfulUpdate is { } t ? DateTimeOffset.UtcNow - t : null }; } }
    public event PropertyChangedEventHandler? PropertyChanged;
    public event EventHandler<SessionSnapshot>? Changed;
    public event EventHandler<PlcFault>? FaultOccurred;
    public event EventHandler<NotificationFailedEventArgs>? NotificationFailed;
    private void Update(Func<SessionSnapshot, SessionSnapshot> change)
    {
        SessionSnapshot snapshot;
        lock (gate) { state = change(state); snapshot = state; }
        Notifications.Raise(PropertyChanged, h => ((PropertyChangedEventHandler)h)(this, new(nameof(Snapshot))), this, NotificationFailed);
        Notifications.Raise(Changed, h => ((EventHandler<SessionSnapshot>)h)(this, snapshot), this, NotificationFailed);
    }
    internal void SetState(SessionState value) => Update(s => s with { State = value });
    internal void BeginAttempt() { lastReported = null; Update(s => s with { State = SessionState.Connecting, NextRetryAt = null }); }
    internal void Contact() => Update(s => s with { LastCommunication = DateTimeOffset.UtcNow });
    internal void Success(TimeSpan duration, int deadlineMs) => Update(s => s with
    {
        State = SessionState.Running, SuccessfulUpdates = s.SuccessfulUpdates + 1,
        LastCommunication = DateTimeOffset.UtcNow, LastSuccessfulUpdate = DateTimeOffset.UtcNow, NextRetryAt = null,
        LastCycleDuration = duration, MaximumCycleDuration = duration > s.MaximumCycleDuration ? duration : s.MaximumCycleDuration,
        MissedDeadlines = s.MissedDeadlines + (duration.TotalMilliseconds > deadlineMs ? 1 : 0)
    });
    internal void Failure(Exception error)
    {
        if (ReferenceEquals(lastReported, error)) return;
        lastReported = error; Volatile.Write(ref lastException, error);
        var typed = error as PlcCommunicationException;
        var fault = new PlcFault(DateTimeOffset.UtcNow, typed?.Operation, typed?.FailureKind, typed?.Gateway, typed?.Route, typed?.TagPath,
            typed?.NativeStatus, typed?.NativeStatusName, typed?.SocketError, typed?.IsTransient ?? PlcErrors.IsCommunication(error), error.Message);
        Update(s => s with { State = SessionState.Faulted, Failures = s.Failures + 1, LastError = error.Message, LastFailure = fault });
        Notifications.Raise(FaultOccurred, h => ((EventHandler<PlcFault>)h)(this, fault), this, NotificationFailed);
    }
    internal void Recover(bool suspended, int delayMs = 0) => Update(s => s with { State = SessionState.Recovering, Recoveries = s.Recoveries + 1, WritesSuspended = suspended, LastRetryDelayMs = delayMs, NextRetryAt = DateTimeOffset.UtcNow.AddMilliseconds(delayMs) });
    internal void ScheduleStart(TimeSpan lateness) => Update(s => s with { LastStartLateness = lateness, MaximumStartLateness = lateness > s.MaximumStartLateness ? lateness : s.MaximumStartLateness });
    internal void SkipIntervals(long count, bool heartbeat = false) => Update(s => heartbeat ? s with { HeartbeatMissedIntervals = s.HeartbeatMissedIntervals + count } : s with { MissedIntervals = s.MissedIntervals + count });
    internal void Overlap() => Update(s => s with { OverlappingRequests = s.OverlappingRequests + 1 });
    internal void Heartbeat(bool value, bool transition) => Update(s => s with { HeartbeatValue = value, LastHeartbeat = transition ? DateTimeOffset.UtcNow : s.LastHeartbeat, HeartbeatTransitions = s.HeartbeatTransitions + (transition ? 1 : 0) });
    internal void Suspend(bool suspended) => Update(s => s with { WritesSuspended = suspended });
    internal void HeartbeatReport(SessionSnapshot report) => Update(s => s with { HeartbeatTransitions = report.HeartbeatTransitions, HeartbeatValue = report.HeartbeatValue,
        LastHeartbeat = report.LastHeartbeat, HeartbeatMissedIntervals = report.HeartbeatMissedIntervals, HeartbeatState = report.State, HeartbeatFailure = report.LastFailure });
    internal void Trigger(TriggerResult result) => Update(s => s with { LastTrigger = new(result.Request, result.Succeeded, result.NotificationsDelivered, result.Error?.Message) });
}

public enum ReconnectDelayMode { Exponential, Fixed }
public enum PendingWritePolicy { Suspend, Retry }
public sealed record RecoveryOptions(int InitialDelayMs = 250, int MaximumDelayMs = 5000, int MaximumAttempts = 0,
    int ConnectionTimeoutMs = 10000, int StaleTimeoutMs = 1000, PendingWritePolicy PendingWrites = PendingWritePolicy.Suspend, ReconnectDelayMode DelayMode = ReconnectDelayMode.Exponential, bool AutoReconnect = true)
{
    public void Validate()
    {
        if (InitialDelayMs < 1 || MaximumDelayMs < InitialDelayMs || MaximumAttempts < 0 || ConnectionTimeoutMs < 1 || StaleTimeoutMs < 1 || !Enum.IsDefined(PendingWrites) || !Enum.IsDefined(DelayMode))
            throw new ArgumentOutOfRangeException(nameof(RecoveryOptions));
    }
    internal int Delay(int attempt) => (int)Math.Min(MaximumDelayMs, DelayMode == ReconnectDelayMode.Fixed ? InitialDelayMs : InitialDelayMs * Math.Pow(2, Math.Min(attempt - 1, 20)));
    internal bool Retry(Exception error, int attempt) => AutoReconnect && (MaximumAttempts == 0 || attempt <= MaximumAttempts) &&
        (error is PlcCommunicationException typed ? typed.IsTransient : error is IOException or TimeoutException or OperationCanceledException || error.GetType().FullName == "libplctag.LibPlcTagException");
}

public interface IRelaySession
{
    SessionKind Kind { get; }
    SyncConfiguration Configuration { get; }
    IPlcModel Model { get; }
    SessionDiagnostics Diagnostics { get; }
    SessionDiagnostics? HeartbeatDiagnostics => null;
    void ResumeWrites();
    Task RunAsync(CancellationToken cancellationToken);
}
