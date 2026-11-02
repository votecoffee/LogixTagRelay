using System.Collections.ObjectModel;

namespace LogixTagRelay;

/// <summary>Independent explicit groups alongside at most one bounded implicit contract.</summary>
public sealed class RelayHost
{
    private readonly Dictionary<string, IRelaySession> sessions = new(StringComparer.OrdinalIgnoreCase);
    private int running;
    private readonly object gate = new();
    /// <summary>Registered named sessions, including faulted sessions. Registration is permitted only while stopped.</summary>
    public IReadOnlyDictionary<string, IRelaySession> Sessions { get; }
    /// <summary>Creates an empty host for multiple explicit contracts and at most one implicit contract.</summary>
    public RelayHost() => Sessions = new ReadOnlyDictionary<string, IRelaySession>(sessions);
    /// <summary>Registers a session, rejecting duplicate names, overlapping write ownership and more than one implicit contract.</summary>
    /// <param name="name">Unique nonempty contract group name.</param>
    /// <param name="session">Session to host independently alongside other registered contracts.</param>
    public void Add(string name, IRelaySession session)
    {
        lock (gate)
        {
        if (Volatile.Read(ref running) != 0) throw new InvalidOperationException("Register contracts before starting the host.");
        if (string.IsNullOrWhiteSpace(name) || sessions.ContainsKey(name)) throw new ArgumentException("Contract names must be unique and nonempty.");
        ModelValidation.Validate(session.Configuration, session.Model);
        if (session.Kind == SessionKind.Implicit && sessions.Values.Any(s => s.Kind == SessionKind.Implicit)) throw new ArgumentException("A host supports one implicit contract, capped at 480 bytes.");
        var writes = WrittenPaths(session).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var existing in sessions.Values)
        {
            if (!existing.Configuration.Gateway.Equals(session.Configuration.Gateway, StringComparison.OrdinalIgnoreCase) || existing.Configuration.Route != session.Configuration.Route) continue;
            if (WrittenPaths(existing).Any(writes.Contains)) throw new ArgumentException("Contracts on the same PLC cannot both own the same write/control path.");
        }
        sessions.Add(name, session);
        }
    }
    /// <summary>Registers an independent explicit contract with a newly owned transport factory and optional recovery policy.</summary>
    /// <param name="name">Unique nonempty contract group name.</param>
    /// <param name="config">Matching generated contract configuration.</param>
    /// <param name="model">Live application model instance used by synchronization.</param>
    /// <param name="connect">Factory returning a newly owned transport and honoring cancellation on each attempt.</param>
    /// <param name="recovery">Optional automatic recovery and uncertain-write policy; defaults apply when null.</param>
    public ExplicitContractSession AddExplicit(string name, SyncConfiguration config, IPlcModel model,
        Func<CancellationToken, ValueTask<IExplicitTransport>> connect, RecoveryOptions? recovery = null)
    {
        var session = new ExplicitContractSession(config, model, connect, recovery); Add(name, session); return session;
    }
    private static IEnumerable<string> WrittenPaths(IRelaySession session) => session.Configuration.Fields.Where(f => f.CanWrite).Select(f => f.Path)
        .Concat(session.Configuration.Heartbeat is { } h ? [h.Tag] : [])
        .Concat(session.Kind == SessionKind.Explicit ? new[] { session.Configuration.Explicit?.MirrorTag, session.Configuration.Explicit?.ResponseTag }.OfType<string>() : []);
    /// <summary>Runs independent contracts until cancellation; healthy sessions continue when another session faults.</summary>
    /// <remarks>Per-session diagnostics report faults immediately. The combined task reports terminal errors after all sessions stop.</remarks>
    /// <param name="cancellationToken">Cancels this operation. Caller cancellation remains cancellation rather than a fabricated communication fault.</param>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        IRelaySession[] registered;
        lock (gate)
        {
            if (Interlocked.Exchange(ref running, 1) != 0) throw new InvalidOperationException("Host is already running.");
            registered = sessions.Values.ToArray();
        }
        try
        {
            // A failed contract remains observable as Faulted; healthy contracts continue until cancellation.
            await Task.WhenAll(registered.Select(async s => await s.RunAsync(cancellationToken).ConfigureAwait(false))).ConfigureAwait(false);
        }
        finally { Volatile.Write(ref running, 0); }
    }
}
