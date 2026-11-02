using System.Collections.ObjectModel;

namespace LogixTagRelay;

/// <summary>Independent explicit groups alongside at most one bounded implicit contract.</summary>
public sealed class RelayHost
{
    private readonly Dictionary<string, IRelaySession> sessions = new(StringComparer.OrdinalIgnoreCase);
    private int running;
    private readonly object gate = new();
    public IReadOnlyDictionary<string, IRelaySession> Sessions { get; }
    public RelayHost() => Sessions = new ReadOnlyDictionary<string, IRelaySession>(sessions);
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
    public ExplicitContractSession AddExplicit(string name, SyncConfiguration config, IPlcModel model,
        Func<CancellationToken, ValueTask<IExplicitTransport>> connect, RecoveryOptions? recovery = null)
    {
        var session = new ExplicitContractSession(config, model, connect, recovery); Add(name, session); return session;
    }
    private static IEnumerable<string> WrittenPaths(IRelaySession session) => session.Configuration.Fields.Where(f => f.CanWrite).Select(f => f.Path)
        .Concat(session.Configuration.Heartbeat is { } h ? [h.Tag] : [])
        .Concat(session.Kind == SessionKind.Explicit ? new[] { session.Configuration.Explicit?.MirrorTag, session.Configuration.Explicit?.ResponseTag }.OfType<string>() : []);
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
