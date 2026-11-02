using System.Text.Json;
using System.Text.Json.Serialization;
using LogixTagRelay;
using LogixTagRelay.Emulation;

var builder = WebApplication.CreateBuilder(args);
builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);
bool selfTest = builder.Configuration.GetValue<bool>("self-test");
if (selfTest) builder.Logging.SetMinimumLevel(LogLevel.Warning);
builder.WebHost.UseUrls(selfTest ? "http://127.0.0.1:0" : "http://127.0.0.1:5275");
builder.WebHost.ConfigureKestrel(o => o.Limits.MaxRequestBodySize = 2 * 1024 * 1024);
builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddSingleton<EmulatorWorkspace>();
builder.Services.AddHostedService<ScriptTicks>();
if (selfTest) builder.Services.AddHostedService<EmulatorSelfTest>();
var app = builder.Build();
var workspace = app.Services.GetRequiredService<EmulatorWorkspace>();
string exampleConfig = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Examples/logixrelay.json"));
string exampleScript = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Examples/motion.js"));
workspace.Load(builder.Configuration["contract"] is { } path ? File.ReadAllText(path) : exampleConfig);
if (builder.Configuration["contract"] is null)
{
    workspace.Current.Memory.Set("Motion.Speed", 10f, "Demo");
    workspace.Current.Scripts.Configure(exampleScript); // Ready to inspect; scripts never auto-start.
}
app.Use(async (context, next) =>
{
    context.Response.Headers["X-Content-Type-Options"] = "nosniff";
    context.Response.Headers["Cache-Control"] = "no-store";
    context.Response.Headers["Content-Security-Policy"] = "default-src 'self'; script-src 'self'; style-src 'self'; frame-ancestors 'none'";
    if (context.Request.Host.Host is not ("127.0.0.1" or "localhost" or "[::1]")) { context.Response.StatusCode = 403; return; }
    if (context.Request.Method == "POST" && context.Request.Headers["X-LogixTagRelay"] != "emulator") { context.Response.StatusCode = 403; return; }
    try { await next(); }
    catch (StaleContractException error) { context.Response.StatusCode = 409; await context.Response.WriteAsJsonAsync(new { error = error.Message }); }
    catch (Exception error) when (error is ArgumentException or FormatException or InvalidOperationException or OverflowException or JsonException)
    { context.Response.StatusCode = 400; await context.Response.WriteAsJsonAsync(new { error = error.Message }); }
});
app.UseDefaultFiles(); app.UseStaticFiles();
app.MapGet("/api/contract", () => workspace.Current.Contract);
app.MapGet("/api/state", () => workspace.Current.Snapshot());
app.MapPost("/api/load", (LoadRequest request) => { workspace.Load(request.ConfigurationJson); return workspace.Current.Snapshot(); });
app.MapPost("/api/example", () =>
{
    workspace.Load(exampleConfig); var session = workspace.Current;
    session.Memory.Set("Motion.Speed", 10f, "Demo"); session.Scripts.Configure(exampleScript); return session.Snapshot();
});
app.MapPost("/api/edit", (EditRequest request) =>
{
    var session = workspace.Require(request.InstanceId); session.Memory.SetText(request.Path, request.Value); return session.Snapshot();
});
app.MapPost("/api/restore", (RestoreRequest request) =>
{
    var session = workspace.Require(request.InstanceId, request.Fingerprint);
    var writes = request.Values.Select(p => new MemoryWrite(p.Key, TagText.Parse(session.Memory.Map(p.Key), p.Value))).ToArray();
    session.Scripts.Stop(); session.Memory.Apply(writes, "GUI restore"); return session.Snapshot();
});
app.MapPost("/api/implicit-permission", (PermissionRequest request) =>
{
    var session = workspace.Require(request.InstanceId);
    if (session.Implicit is null) throw new InvalidOperationException("Contract has no implicit settings.");
    session.Implicit.ApplyCommands = request.Enabled; return session.Snapshot();
});
app.MapPost("/api/script", (ScriptRequest request) =>
{
    var session = workspace.Require(request.InstanceId);
    if (request.Action == "stop") session.Scripts.Stop();
    else
    {
        if (request.Action is not ("start" or "step" or "save")) throw new ArgumentException("Unknown script action.");
        session.Scripts.Configure(request.Source, request.IntervalMs, request.Action == "start");
        if (request.Action == "step") session.Scripts.Step(request.IntervalMs / 1000d);
    }
    return session.Snapshot();
});
app.MapPost("/api/client/read", async (ClientTagRequest request, CancellationToken ct) =>
{
    var session = workspace.Require(request.InstanceId, request.Fingerprint);
    var value = await session.Explicit.ReadAsync(request.Path, request.Type, ct); Interlocked.Increment(ref session.Reads);
    return new TagReply(TagText.Format(value));
});
app.MapPost("/api/client/write", async (ClientTagRequest request, CancellationToken ct) =>
{
    var session = workspace.Require(request.InstanceId, request.Fingerprint);
    if (request.Value is null) throw new ArgumentException("A value is required.");
    var field = session.Memory.Map(request.Path);
    await session.Explicit.WriteAsync(request.Path, request.Type, TagText.Parse(field, request.Value), ct); Interlocked.Increment(ref session.Writes);
    return new TagReply(TagText.Format(session.Memory.Read(request.Path)));
});
app.MapPost("/api/client/exchange", (ClientImageRequest request) =>
{
    var session = workspace.Require(request.InstanceId, request.Fingerprint);
    if (session.Implicit is null) throw new InvalidOperationException("Contract has no implicit settings.");
    return new ImageReply(session.Implicit.Exchange(request.Frame));
});
app.Run();

sealed class StaleContractException() : Exception("The emulator contract was replaced or differs from this client. Reconnect using the loaded JSON.");
sealed class EmulatorWorkspace
{
    private EmulatorSession? current;
    public EmulatorSession Current => Volatile.Read(ref current) ?? throw new InvalidOperationException("Load a contract first.");
    public void Load(string json)
    {
        var replacement = new EmulatorSession(SyncConfiguration.FromJson(json));
        Interlocked.Exchange(ref current, replacement)?.Scripts.Stop();
    }
    public EmulatorSession Require(Guid instance, string? fingerprint = null)
    {
        var session = Current;
        if (session.Contract.InstanceId != instance || (fingerprint is not null && session.Contract.Fingerprint != fingerprint)) throw new StaleContractException();
        return session;
    }
}
sealed class EmulatorSession
{
    public PlcMemory Memory { get; }
    public MemoryExplicitTransport Explicit { get; }
    public ImplicitPlc? Implicit { get; }
    public ScriptRunner Scripts { get; }
    public EmulationContract Contract { get; }
    public long Reads, Writes;
    public EmulatorSession(SyncConfiguration config)
    {
        Memory = new(config); Explicit = new(Memory); Scripts = new(Memory);
        if (config.Implicit is not null) Implicit = new(Memory);
        Contract = new(Guid.NewGuid(), Memory.Fingerprint, config.ToJson());
    }
    public object Snapshot() => new { Contract, Memory = Memory.Snapshot(), Script = Scripts.Status, Implicit = Implicit?.Status,
        ExplicitReads = Interlocked.Read(ref Reads), ExplicitWrites = Interlocked.Read(ref Writes) };
}
sealed class ScriptTicks(EmulatorWorkspace workspace) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(20));
        while (await timer.WaitForNextTickAsync(stoppingToken)) workspace.Current.Scripts.TickIfDue(DateTimeOffset.UtcNow);
    }
}
sealed record LoadRequest(string ConfigurationJson);
sealed record EditRequest(Guid InstanceId, string Path, string Value);
sealed record PermissionRequest(Guid InstanceId, bool Enabled);
sealed record RestoreRequest(Guid InstanceId, string Fingerprint, Dictionary<string, string> Values);
sealed record ScriptRequest(Guid InstanceId, string Action, string Source = "", int IntervalMs = 100);
