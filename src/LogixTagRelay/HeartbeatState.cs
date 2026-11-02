namespace LogixTagRelay;

internal sealed class HeartbeatState(SessionDiagnostics diagnostics)
{
    private bool phase;
    public void Seed(bool current) { phase = current; diagnostics.Heartbeat(current, false); }
    public bool Prepare() => !phase;
    public void Confirm(bool current) { bool changed = phase != current; phase = current; diagnostics.Heartbeat(current, changed); }
}
