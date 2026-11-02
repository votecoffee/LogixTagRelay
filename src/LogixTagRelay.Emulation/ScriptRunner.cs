using System.Text.Json;
using Jint;

namespace LogixTagRelay.Emulation;

public sealed record ScriptStatus(string Source, bool Running, int IntervalMs, long Ticks, string Outcome,
    string? Error, string StateJson, IReadOnlyList<string> Log);

/// <summary>Transactional JavaScript ticks over PLC memory. Only primitive JSON crosses the engine boundary.</summary>
public sealed class ScriptRunner(PlcMemory memory)
{
    private readonly object gate = new();
    private string source = "", stateJson = "{}", outcome = "Stopped";
    private string? error;
    private bool running;
    private int intervalMs = 100;
    private long ticks;
    private Dictionary<string, string>? previous;
    private readonly Queue<string> log = new();
    private DateTimeOffset due;
    public ScriptStatus Status { get { lock (gate) return new(source, running, intervalMs, ticks, outcome, error, stateJson, log.ToArray()); } }
    public void Configure(string code, int interval = 100, bool start = false)
    {
        ArgumentNullException.ThrowIfNull(code);
        if (code.Length > 65536) throw new ArgumentException("Scripts are limited to 64 KiB characters.");
        if (interval is < 20 or > 60000) throw new ArgumentOutOfRangeException(nameof(interval), "Tick interval must be 20..60000 ms.");
        lock (gate)
        {
            if (source != code) { stateJson = "{}"; previous = null; ticks = 0; log.Clear(); }
            source = code; intervalMs = interval; running = start; error = null; outcome = start ? "Running" : "Ready"; due = DateTimeOffset.UtcNow;
        }
    }
    public void Stop() { lock (gate) { running = false; outcome = "Stopped"; } }
    public void TickIfDue(DateTimeOffset now)
    {
        lock (gate)
        {
            if (!running || now < due) return;
            due = now.AddMilliseconds(intervalMs);
            Step(intervalMs / 1000d);
        }
    }
    public bool Step(double deltaSeconds = 0.1)
    {
        if (!double.IsFinite(deltaSeconds) || deltaSeconds <= 0 || deltaSeconds > 60) throw new ArgumentOutOfRangeException(nameof(deltaSeconds));
        lock (gate)
        {
            try
            {
                var snapshot = memory.Snapshot();
                var tags = snapshot.Tags.ToDictionary(t => t.Map.Path, t => new { type = t.Map.Type.ToString(), value = t.Value }, StringComparer.OrdinalIgnoreCase);
                using var savedState = JsonDocument.Parse(stateJson);
                string payload = JsonSerializer.Serialize(new { tags, previous, dt = deltaSeconds, state = savedState.RootElement });
                // No CLR objects/delegates, filesystem, network, modules or host code are exposed.
                // Include cold interpreter/JIT work in a one-second wall limit; statements bound accidental loops.
                using var engine = new Engine(o => o.Strict().LimitMemory(16_000_000).TimeoutInterval(TimeSpan.FromSeconds(1)).MaxStatements(20000).LimitRecursion(64));
                engine.SetValue("__input", payload);
                engine.Execute(Prelude + "\n(function(plc, dt, state) {\n" + source + "\n})(plc, input.dt, input.state);\n" + Epilogue);
                string output = engine.GetValue("__output").AsString();
                if (output.Length > 262144) throw new InvalidOperationException("Script output/state exceeds 256 KiB.");
                using var result = JsonDocument.Parse(output);
                var writes = result.RootElement.GetProperty("writes").EnumerateObject().Select(p =>
                    new MemoryWrite(p.Name, TagText.Parse(memory.Map(p.Name), p.Value.GetString()!))).ToArray();
                if (writes.Length > 1024) throw new InvalidOperationException("A script tick may write at most 1024 tags.");
                var state = result.RootElement.GetProperty("state");
                if (state.ValueKind != JsonValueKind.Object) throw new InvalidOperationException("Script state must remain a JSON object.");
                string nextState = state.GetRawText();
                var nextLog = result.RootElement.GetProperty("log").EnumerateArray().Select(l => l.GetString() ?? throw new InvalidOperationException("Invalid script log.")).Take(100)
                    .Select(l => l[..Math.Min(l.Length, 512)]).ToArray();
                lock (memory.Gate)
                {
                    if (!memory.Apply(writes, "Script", snapshot.Revision)) { outcome = "Memory changed during tick; retry next tick"; return false; }
                    previous = memory.Snapshot().Tags.ToDictionary(t => t.Map.Path.ToUpperInvariant(), t => t.Value);
                }
                stateJson = nextState;
                foreach (var line in nextLog)
                {
                    log.Enqueue(line); while (log.Count > 100) log.Dequeue();
                }
                ticks++; error = null; outcome = "Tick committed";
                return true;
            }
            catch (Exception failure)
            {
                running = false; error = failure.Message[..Math.Min(failure.Message.Length, 1024)]; outcome = "Stopped on error";
                return false;
            }
        }
    }
    private const string Prelude = """
        const input = JSON.parse(__input);
        const tags = Object.create(null);
        for (const [path, tag] of Object.entries(input.tags)) tags[path.toUpperCase()] = { ...tag, path };
        const writes = Object.create(null), lines = [];
        function tag(path) {
          if (typeof path !== 'string' || !Object.hasOwn(tags, path.toUpperCase())) throw new Error('Unknown tag: ' + path);
          return tags[path.toUpperCase()];
        }
        function read(t) {
          const value = Object.hasOwn(writes, t.path) ? writes[t.path] : t.value;
          if (t.type === 'STRING') return value;
          if (t.type === 'BOOL') return value === 'true';
          if (t.type === 'LINT') return BigInt(value);
          return Number(value);
        }
        const plc = Object.freeze({
          has(path) { return typeof path === 'string' && Object.hasOwn(tags, path.toUpperCase()); },
          get(path) { return read(tag(path)); },
          set(path, value) {
            const t = tag(path);
            if (t.type === 'STRING' && typeof value !== 'string') throw new Error('STRING requires a string');
            if (t.type === 'BOOL' && typeof value !== 'boolean') throw new Error('BOOL requires a boolean');
            if (t.type === 'LINT') {
              if (typeof value !== 'bigint' && typeof value !== 'string' && !(typeof value === 'number' && Number.isSafeInteger(value))) throw new Error('LINT requires BigInt, integer text, or a safe integer');
            } else if (t.type !== 'STRING' && t.type !== 'BOOL') {
              if (typeof value !== 'number' || (t.type !== 'REAL' && !Number.isSafeInteger(value))) throw new Error('Numeric tag requires a valid number');
            }
            writes[t.path] = String(value);
          },
          changed(path) { const t = tag(path); return input.previous === null || input.previous[t.path.toUpperCase()] !== t.value; },
          log(value) { if (lines.length < 100) lines.push(String(value).slice(0, 512)); }
        });
        """;
    private const string Epilogue = "globalThis.__output = JSON.stringify({ writes, log: lines, state: input.state });";
}
