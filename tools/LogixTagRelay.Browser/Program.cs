using System.IO.Compression;
using System.Text;
using System.Text.Json.Serialization;
using LogixTagRelay.Design;
using LogixTagRelay.Logix;
using LogixTagRelay;

var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = args, ContentRootPath = AppContext.BaseDirectory });
bool selfTest = args.Contains("--self-test");
builder.WebHost.UseUrls(selfTest ? "http://127.0.0.1:0" : "http://127.0.0.1:5274");
if (selfTest) builder.Services.AddHostedService<BrowserSelfTest>();
builder.WebHost.ConfigureKestrel(o => o.Limits.MaxRequestBodySize = 16 * 1024 * 1024);
builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
var app = builder.Build();
app.Use(async (context, next) =>
{
    context.Response.Headers["X-Content-Type-Options"] = "nosniff";
    context.Response.Headers["Content-Security-Policy"] = "default-src 'self'; script-src 'self'; style-src 'self'; frame-ancestors 'none'";
    if (context.Request.Method == "POST" && context.Request.Headers["X-LogixTagRelay"] != "browser")
    { context.Response.StatusCode = 403; return; }
    try { await next(); }
    catch (Exception error) when (error is ArgumentException or FormatException or System.Xml.XmlException or InvalidOperationException or System.Text.Json.JsonException)
    { context.Response.StatusCode = 400; await context.Response.WriteAsJsonAsync(new { error = error.Message }); }
    catch (Exception error) when (error is IOException or libplctag.LibPlcTagException or OperationCanceledException)
    { context.Response.StatusCode = 502; await context.Response.WriteAsJsonAsync(new { error = error.Message }); }
});
app.UseDefaultFiles(); app.UseStaticFiles();
app.MapPost("/api/catalog", async (HttpRequest request) =>
{
    var form = await request.ReadFormAsync();
    var file = form.Files.GetFile("file") ?? throw new ArgumentException("Choose an L5X export.");
    using var stream = file.OpenReadStream();
    return Results.Json(L5xCatalog.Read(stream));
});
app.MapPost("/api/online", async (OnlineRequest request, CancellationToken cancellationToken) =>
    Results.Json(await new LogixBrowser(request.Gateway, request.Route).BrowseAsync(cancellationToken)));
app.MapPost("/api/module-template", async (HttpRequest request) =>
{
    var form = await request.ReadFormAsync();
    var file = form.Files.GetFile("file") ?? throw new ArgumentException("Choose a native module L5X export.");
    using var stream = new MemoryStream(); await file.CopyToAsync(stream);
    var bytes = stream.ToArray();
    return Results.Json(new { info = ModuleTemplate.Inspect(bytes), templateBase64 = Convert.ToBase64String(bytes) });
});
app.MapPost("/api/project", async (HttpRequest http, CancellationToken ct) =>
{
    using var reader = new StreamReader(http.Body);
    var request = ContractProject.FromJson(await reader.ReadToEndAsync(ct));
    if (request.Catalog is null) throw new ArgumentException("Browser projects require an embedded catalog. Use the build CLI for sourceL5x projects.");
    var bundle = request.Generate();
    return Results.Json(new { project = request, catalog = request.Catalog, configuration = bundle.Configuration });
});
app.MapPost("/api/generate", (GenerateRequest request) =>
{
    var project = new ContractProject(request.ProjectName, request.Selections, request.Catalog, Namespace: request.Namespace, ClassName: request.ClassName,
        IncludeImplicit: request.Implicit, ModuleName: request.ModuleName, SoftwareRevision: request.SoftwareRevision, AssemblyBytes: request.AssemblyBytes,
        Explicit: request.Explicit, Implicit: request.AssemblyOptions, Gateway: request.Gateway, Route: request.Route, PcAddress: request.PcAddress);
    var bundle = project.Generate();
    ModuleImport? module = request.Implicit && request.ModuleTemplateBase64 != null ?
        ModuleTemplate.Generate(Convert.FromBase64String(request.ModuleTemplateBase64), bundle.Configuration, request.ModuleName, request.PcAddress) : null;
    using var bytes = new MemoryStream();
    using (var zip = new ZipArchive(bytes, ZipArchiveMode.Create, true))
    {
        void Add(string name, string content)
        { using var writer = new StreamWriter(zip.CreateEntry(name).Open(), new UTF8Encoding(false)); writer.Write(content); }
        Add("contract.ltrx.json", project.ToJson());
        Add("GeneratedTags.cs", bundle.ModelCs); Add("logixrelay.json", bundle.Configuration.ToJson());
        if (request.Implicit)
        {
            Add("RelayRoutine.L5X", bundle.RoutineL5x); Add("ModuleSetup.txt", bundle.ModuleInstructions);
            Add("LTR_SintMerge.L5X", bundle.MergeAoiL5x); Add("LTR_SintMerge.st", bundle.MergeStructuredText);
        }
        if (module != null)
        {
            Add("GenericEthernetModule.L5X", module.Xml);
            Add("ModuleProvenance.txt", $"Native template SHA-256: {module.SourceSha256}\nGenerated as an offline review copy. Studio 5000 import/verify/re-export acceptance is still required.\n");
        }
    }
    return Results.File(bytes.ToArray(), "application/zip", "LogixTagRelay.Generated.zip");
});
app.Run();
public sealed record OnlineRequest(string Gateway, string Route = "1,0");
public sealed record GenerateRequest(TagCatalog Catalog, List<TagSelection> Selections, bool Implicit = true, string ModuleName = "LogixRelay", string SoftwareRevision = "",
    int AssemblyBytes = 0, string? ModuleTemplateBase64 = null, string PcAddress = "192.168.1.200", ExplicitOptions? Explicit = null, ImplicitOptions? AssemblyOptions = null, string ProjectName = "Contract", string Namespace = "LogixTagRelay.Generated", string ClassName = "GeneratedTags", string Gateway = "192.168.1.10", string Route = "1,0");
