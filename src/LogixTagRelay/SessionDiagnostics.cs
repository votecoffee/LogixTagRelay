using System.ComponentModel;

namespace LogixTagRelay;

/// <summary>Lifecycle of an individual synchronization channel.</summary>
public enum SessionState
{
    /// <summary>The channel is not running.</summary>
    Stopped,
    /// <summary>The channel is establishing its transport.</summary>
    Connecting,
    /// <summary>Implicit communication is waiting for a fresh matched baseline.</summary>
    AwaitingBaseline,
    /// <summary>The channel is exchanging data successfully.</summary>
    Running,
    /// <summary>The channel is waiting or retrying after a transient fault.</summary>
    Recovering,
    /// <summary>The channel stopped because of a terminal fault or exhausted retries.</summary>
    Faulted,
}
/// <summary>Transport used for contract data; heartbeat always uses explicit communication.</summary>
public enum SessionKind
{
    /// <summary>Addressed explicit tag reads and writes.</summary>
    Explicit,
    /// <summary>One bounded cyclic SINT image in each direction.</summary>
    Implicit,
}
/// <summary>Serializable outcome of a DINT-triggered transfer and its completion notifications.</summary>
/// <param name="Request">Nonzero DINT request value handled by this transfer.</param>
/// <param name="Succeeded">Whether the requested data transfer succeeded.</param>
/// <param name="NotificationsDelivered">Whether all configured handled and ACK/NACK writes completed.</param>
/// <param name="Error">Failure details, or null on success.</param>
public sealed record TriggerStatus(int Request, bool Succeeded, bool NotificationsDelivered, string? Error);
/// <summary>Immutable channel diagnostics. Data age is recomputed when Snapshot is queried.</summary>
/// <param name="State">Current lifecycle state.</param>
/// <param name="SuccessfulUpdates">Completed data cycles; held-trigger polls do not count as new data updates.</param>
/// <param name="Failures">Recorded communication failures.</param>
/// <param name="Recoveries">Automatic reconnect attempts.</param>
/// <param name="MissedDeadlines">Successful cycles whose work duration exceeded the configured interval.</param>
/// <param name="LastCommunication">Most recent successful transport contact.</param>
/// <param name="LastSuccessfulUpdate">Most recent completed data synchronization.</param>
/// <param name="DataAge">Time since the last successful data synchronization, or null before success.</param>
/// <param name="LastCycleDuration">Duration of the latest successful data cycle.</param>
/// <param name="MaximumCycleDuration">Largest observed successful data cycle duration.</param>
/// <param name="LastError">Most recent fault message, when any.</param>
/// <param name="WritesSuspended">Whether retained application edits require explicit resumption before transmission.</param>
/// <param name="LastTrigger">Most recent DINT trigger outcome, when configured and handled.</param>
public sealed record SessionSnapshot(SessionState State, long SuccessfulUpdates, long Failures, long Recoveries,
    long MissedDeadlines, DateTimeOffset? LastCommunication, DateTimeOffset? LastSuccessfulUpdate,
    TimeSpan? DataAge, TimeSpan LastCycleDuration, TimeSpan MaximumCycleDuration, string? LastError,
    bool WritesSuspended, TriggerStatus? LastTrigger)
{
    /// <summary>Most recent structured communication failure, retained across recovery for diagnosis.</summary>
    public PlcFault? LastFailure { get; init; }
    /// <summary>Scheduled data starts skipped while prior work occupied their slots; no catch-up bursts are sent.</summary>
    public long MissedIntervals { get; init; }
    /// <summary>External or manual requests queued because the serialized session gate was busy.</summary>
    public long OverlappingRequests { get; init; }
    /// <summary>Observed delay of the latest cycle start relative to its monotonic schedule.</summary>
    public TimeSpan LastStartLateness { get; init; }
    /// <summary>Largest observed start delay for this session instance.</summary>
    public TimeSpan MaximumStartLateness { get; init; }
    /// <summary>Most recently selected automatic reconnect delay in milliseconds.</summary>
    public int LastRetryDelayMs { get; init; }
    /// <summary>Planned automatic retry timestamp, or null when no retry is pending.</summary>
    public DateTimeOffset? NextRetryAt { get; init; }
    /// <summary>Successful explicit heartbeat toggles since this diagnostics instance was created.</summary>
    public long HeartbeatTransitions { get; init; }
    /// <summary>Last observed or successfully written explicit heartbeat BOOL, or null before seeding.</summary>
    public bool? HeartbeatValue { get; init; }
    /// <summary>Timestamp of the most recent successful explicit heartbeat write.</summary>
    public DateTimeOffset? LastHeartbeat { get; init; }
    /// <summary>Scheduled heartbeat slots skipped independently of data interval counters.</summary>
    public long HeartbeatMissedIntervals { get; init; }
    /// <summary>Lifecycle of the separate explicit heartbeat channel when applicable.</summary>
    public SessionState? HeartbeatState { get; init; }
    /// <summary>Most recent fault in the separate explicit heartbeat channel.</summary>
    public PlcFault? HeartbeatFailure { get; init; }
}

/// <summary>Thread-safe observable diagnostics. Subscriber exceptions are isolated; handlers run on the updating thread.</summary>
public sealed class SessionDiagnostics : INotifyPropertyChanged
{
    private readonly object gate = new();
    private SessionSnapshot state = new(SessionState.Stopped, 0, 0, 0, 0, null, null, null, TimeSpan.Zero, TimeSpan.Zero, null, false, null);
    private Exception? lastException, lastReported;
    /// <summary>Original most recent exception, preserving typed fault metadata and its inner cause.</summary>
    public Exception? LastException => Volatile.Read(ref lastException);
    /// <summary>Current immutable diagnostics with data age evaluated at the time of access.</summary>
    public SessionSnapshot Snapshot { get { lock (gate) return state with { DataAge = state.LastSuccessfulUpdate is { } t ? DateTimeOffset.UtcNow - t : null }; } }
    /// <summary>Raised for changed observable properties on the updating thread; identical value reads remain silent.</summary>
    public event PropertyChangedEventHandler? PropertyChanged;
    /// <summary>Raised when diagnostics change, on the updating thread.</summary>
    public event EventHandler<SessionSnapshot>? Changed;
    /// <summary>Raised for a newly reported structured communication fault, on the updating thread.</summary>
    public event EventHandler<PlcFault>? FaultOccurred;
    /// <summary>Reports isolated subscriber exceptions without changing the communication result.</summary>
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

/// <summary>Automatic reconnect delay schedule.</summary>
public enum ReconnectDelayMode
{
    /// <summary>Grows from InitialDelayMs up to MaximumDelayMs.</summary>
    Exponential,
    /// <summary>Uses InitialDelayMs for every retry.</summary>
    Fixed,
}
/// <summary>How retained application edits are handled after an uncertain communication failure.</summary>
public enum PendingWritePolicy
{
    /// <summary>Retains edits and refreshes PLC readback, requiring ResumeWrites before retransmission.</summary>
    Suspend,
    /// <summary>Permits pending desired values to be retried automatically after recovery.</summary>
    Retry,
}
/// <summary>Reconnect timing, limits, freshness and uncertain-write policy. Defaults to automatic exponential retries and suspended pending writes.</summary>
/// <param name="InitialDelayMs">Initial reconnect delay, or the delay for every fixed-mode retry, in milliseconds.</param>
/// <param name="MaximumDelayMs">Upper bound for exponential retry delay, at least InitialDelayMs.</param>
/// <param name="MaximumAttempts">Maximum consecutive recovery attempts after the initial connection; zero allows unlimited attempts.</param>
/// <param name="ConnectionTimeoutMs">Deadline for establishing a transport, in milliseconds.</param>
/// <param name="StaleTimeoutMs">Maximum accepted implicit input age, in milliseconds.</param>
/// <param name="PendingWrites">Policy for uncertain retained application writes after a fault; defaults to Suspend.</param>
/// <param name="DelayMode">Fixed or exponential reconnect schedule.</param>
/// <param name="AutoReconnect">Whether transient communication faults may trigger automatic reconnect.</param>
public sealed record RecoveryOptions(int InitialDelayMs = 250, int MaximumDelayMs = 5000, int MaximumAttempts = 0,
    int ConnectionTimeoutMs = 10000, int StaleTimeoutMs = 1000, PendingWritePolicy PendingWrites = PendingWritePolicy.Suspend, ReconnectDelayMode DelayMode = ReconnectDelayMode.Exponential, bool AutoReconnect = true)
{
    /// <summary>Rejects invalid timing, retry limits and policy values with ArgumentOutOfRangeException.</summary>
    public void Validate()
    {
        if (InitialDelayMs < 1 || MaximumDelayMs < InitialDelayMs || MaximumAttempts < 0 || ConnectionTimeoutMs < 1 || StaleTimeoutMs < 1 || !Enum.IsDefined(PendingWrites) || !Enum.IsDefined(DelayMode))
            throw new ArgumentOutOfRangeException(nameof(RecoveryOptions));
    }
    internal int Delay(int attempt) => (int)Math.Min(MaximumDelayMs, DelayMode == ReconnectDelayMode.Fixed ? InitialDelayMs : InitialDelayMs * Math.Pow(2, Math.Min(attempt - 1, 20)));
    internal bool Retry(Exception error, int attempt) => AutoReconnect && (MaximumAttempts == 0 || attempt <= MaximumAttempts) &&
        (error is PlcCommunicationException typed ? typed.IsTransient : error is IOException or TimeoutException or OperationCanceledException || error.GetType().FullName == "libplctag.LibPlcTagException");
}

/// <summary>A hosted contract with independent recovery, diagnostics and write suspension.</summary>
public interface IRelaySession
{
    /// <summary>Transport kind used by the session.</summary>
    SessionKind Kind { get; }
    /// <summary>A defensive copy of the session&apos;s contract configuration.</summary>
    SyncConfiguration Configuration { get; }
    /// <summary>The application&apos;s live generated model instance shared with this session.</summary>
    IPlcModel Model { get; }
    /// <summary>Communication state, freshness, faults, retry and interval counters for this session.</summary>
    SessionDiagnostics Diagnostics { get; }
    /// <summary>Explicit heartbeat diagnostics, or null when heartbeat is not configured.</summary>
    SessionDiagnostics? HeartbeatDiagnostics => null;
    /// <summary>Explicitly permits retained application edits to be transmitted after write suspension; reconcile fresh PLC readback first.</summary>
    void ResumeWrites();
    /// <summary>Runs until cancellation or a terminal fault. Cancel and await completion before disposing owned resources.</summary>
    /// <param name="cancellationToken">Cancels this operation. Caller cancellation remains cancellation rather than a fabricated communication fault.</param>
    Task RunAsync(CancellationToken cancellationToken);
}
