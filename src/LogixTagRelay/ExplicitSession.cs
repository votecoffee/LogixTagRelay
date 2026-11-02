namespace LogixTagRelay;

public interface IExplicitTransport : IAsyncDisposable
{
    ValueTask<object> ReadAsync(string path, PlcType type, CancellationToken cancellationToken);
    ValueTask WriteAsync(string path, PlcType type, object value, CancellationToken cancellationToken);
    ValueTask<object> ReadAsync(FieldMap field, CancellationToken cancellationToken) => ReadAsync(field.Path, field.Type, cancellationToken);
    ValueTask WriteAsync(FieldMap field, object value, CancellationToken cancellationToken) => WriteAsync(field.Path, field.Type, value, cancellationToken);
}

/// <summary>Single serialized group. Owns no transport lifetime; the caller disposes its transport.</summary>
public sealed class ExplicitSession
{
    private readonly SyncConfiguration config;
    private readonly IPlcModel model;
    private readonly IExplicitTransport transport;
    private readonly SemaphoreSlim cycleGate = new(1, 1);
    private int running;
    private int? completedTrigger;
    public ExplicitSession(SyncConfiguration config, IPlcModel model, IExplicitTransport transport)
    {
        ModelValidation.Validate(config, model);
        this.config = config; this.model = model; this.transport = transport;
    }
    public async Task SynchronizeOnceAsync(CancellationToken cancellationToken = default)
    {
        await cycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Stage ALL reads before publishing them. No multi-tag PLC atomicity is implied.
            var reads = new List<(IPlcVariable Variable, object Value)>();
            foreach (var variable in model.Variables)
            {
                var value = await transport.ReadAsync(variable.Map, cancellationToken).ConfigureAwait(false);
                if (variable.Map.Type == PlcType.STRING) PlcTypes.ValidateString((string)value, variable.Map.StringCapacity);
                reads.Add((variable, value));
            }
            var time = DateTimeOffset.UtcNow;
            foreach (var (variable, value) in reads) variable.Observe(value, time);
            foreach (var variable in model.Variables.Where(v => v.Map.Direction == Direction.AppToPlc && v.Dirty))
            {
                var captured = variable.Capture();
                try
                {
                    await transport.WriteAsync(variable.Map, captured.Value, cancellationToken).ConfigureAwait(false);
                    variable.AcceptWrite(captured.Version, DateTimeOffset.UtcNow);
                }
                catch { variable.MarkBad(); throw; }
            }
        }
        catch
        {
            foreach (var variable in model.Variables) variable.MarkBad();
            throw;
        }
        finally { cycleGate.Release(); }
    }
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref running, 1) != 0) throw new InvalidOperationException("Session is already running.");
        var options = config.Explicit ?? new ExplicitOptions();
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(options.TriggerTag is null ? options.IntervalMs : options.TriggerPollMs));
            // First cycle is immediate; subsequent cycles use a fixed cadence with no overlap.
            do
            {
                if (options.TriggerTag is null) await SynchronizeOnceAsync(cancellationToken).ConfigureAwait(false);
                else
                {
                    var sequence = (int)await transport.ReadAsync(options.TriggerTag, PlcType.DINT, cancellationToken).ConfigureAwait(false);
                    if (completedTrigger != sequence)
                    {
                        await SynchronizeOnceAsync(cancellationToken).ConfigureAwait(false);
                        if (options.AcknowledgeTag is not null)
                            await transport.WriteAsync(options.AcknowledgeTag, PlcType.DINT, sequence, cancellationToken).ConfigureAwait(false);
                        completedTrigger = sequence; // Never acknowledge a failed group.
                    }
                }
            } while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false));
        }
        finally { Volatile.Write(ref running, 0); }
    }
}
