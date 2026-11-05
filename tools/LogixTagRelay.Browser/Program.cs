using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
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
app.MapGet("/api/image-layout", () => Results.Json(new
{
    headerBytes = ImplicitImage.HeaderBytes,
    alignment = 4,
    maxBytes = new ImplicitOptions().MaxAssemblyBytes,
    stringOverhead = PlcTypes.Size(PlcType.STRING, 1) - 1,
    scalarBytes = Enum.GetValues<PlcType>().Where(type => type != PlcType.STRING)
        .ToDictionary(type => type.ToString(), type => PlcTypes.Size(type))
}));
app.MapPost("/api/module-template", async (HttpRequest request) =>
{
    var form = await request.ReadFormAsync();
    var file = form.Files.GetFile("file") ?? throw new ArgumentException("Choose a native module L5X export.");
    using var stream = new MemoryStream(); await file.CopyToAsync(stream);
    var bytes = stream.ToArray();
    var settings = form.TryGetValue("implicit", out var value)
        ? JsonSerializer.Deserialize<ImplicitOptions>(value.ToString(), SyncConfiguration.Json)
        : null;
    return Results.Json(new { info = ModuleTemplate.Inspect(bytes, settings), templateBase64 = Convert.ToBase64String(bytes) });
});
app.MapPost("/api/project", async (HttpRequest http, CancellationToken ct) =>
{
    ContractProject request;
    if (http.HasFormContentType)
    {
        var form = await http.ReadFormAsync(ct);
        request = ContractProject.FromJson(form["project"].ToString());
        if (request.SourceL5x is null || request.Catalog is not null)
            throw new ArgumentException("An accompanying L5X file is only needed for a project that references sourceL5x.");
        var file = form.Files.GetFile("file") ?? throw new ArgumentException("Choose the L5X export referenced by the project.");
        if (request.SourceSha256 is { } expected)
        {
            using var source = file.OpenReadStream();
            if (!Convert.ToHexString(await SHA256.HashDataAsync(source, ct)).Equals(expected, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("The selected L5X export does not match the SHA-256 pinned in the project.");
        }
        using var input = file.OpenReadStream();
        request = request with { Catalog = L5xCatalog.Read(input), SourceL5x = null, SourceSha256 = null };
    }
    else
    {
        using var reader = new StreamReader(http.Body);
        request = ContractProject.FromJson(await reader.ReadToEndAsync(ct));
        if (request.Catalog is null || request.SourceL5x is not null)
            throw new ArgumentException("Import this project with the L5X export it references.");
    }
    var bundle = request.Generate();
    return Results.Json(new { project = request, catalog = request.Catalog, configuration = bundle.Configuration });
});
app.MapPost("/api/generate", (GenerateRequest request) =>
{
    var project = new ContractProject(request.ProjectName, request.Selections, request.Catalog, Namespace: request.Namespace, ClassName: request.ClassName,
        IncludeImplicit: request.Implicit, ModuleName: request.ModuleName, SoftwareRevision: request.SoftwareRevision, AssemblyBytes: request.AssemblyBytes,
        Explicit: request.Explicit, Implicit: request.AssemblyOptions, Gateway: request.Gateway, Route: request.Route, PcAddress: request.PcAddress, Heartbeat: request.Heartbeat);
    using var bytes = new MemoryStream();
    using (var zip = new ZipArchive(bytes, ZipArchiveMode.Create, true))
        AddContract(zip, "", project, project.Generate(), request.ModuleTemplateBase64);
    return Results.File(bytes.ToArray(), "application/zip", "LogixTagRelay.Generated.zip");
});
app.MapPost("/api/workspace", (WorkspaceRequest request) => Results.Json(GenerateWorkspace(request).Select(contract => new
{
    project = contract.Project,
    configuration = contract.Bundle.Configuration,
    template = contract.Template is { } template ? new { template.Base64, template.Filename, info = contract.TemplateInfo } : null
})));
app.MapPost("/api/workspace/generate", (WorkspaceRequest request) =>
{
    var contracts = GenerateWorkspace(request);
    using var bytes = new MemoryStream();
    using (var zip = new ZipArchive(bytes, ZipArchiveMode.Create, true))
    {
        WriteEntry(zip, "LogixTagRelay.workspace.json", JsonSerializer.Serialize(request, SyncConfiguration.Json) + "\n");
        for (int index = 0; index < contracts.Count; index++)
        {
            var contract = contracts[index];
            AddContract(zip, $"contracts/{index + 1:D2}-{contract.Project.ClassName}/", contract.Project, contract.Bundle, contract.Template?.Base64);
        }
    }
    return Results.File(bytes.ToArray(), "application/zip", "LogixTagRelay.Interface.zip");
});
app.Run();

static List<WorkspaceContract> GenerateWorkspace(WorkspaceRequest request)
{
    if (request.WorkspaceVersion != 1 || request.Contracts is not { Count: > 0 })
        throw new ArgumentException("A version 1 workspace with at least one contract is required.");
    var projects = request.Contracts.Select(entry =>
    {
        if (entry?.Project.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("Each workspace entry must contain a contract project.");
        var project = ContractProject.FromJson(entry.Project.GetRawText());
        if (project.Catalog is null || project.SourceL5x is not null)
            throw new ArgumentException("Workspace contracts must embed their catalogs. Import linked contracts individually with their L5X files first.");
        return (Project: project, Template: entry.ModuleTemplate);
    }).ToList();
    if (projects.Count(entry => entry.Project.IncludeImplicit) > 1)
        throw new ArgumentException("A workspace can have only one implicit contract.");
    return projects.Select(entry =>
    {
        ModuleTemplateInfo? info = null;
        if (entry.Template is { } template)
        {
            if (!entry.Project.IncludeImplicit || string.IsNullOrWhiteSpace(template.Filename) || string.IsNullOrWhiteSpace(template.Base64))
                throw new ArgumentException("A module attachment requires an implicit contract and a named L5X export.");
            info = ModuleTemplate.Inspect(Convert.FromBase64String(template.Base64), entry.Project.Implicit);
            if (info.Errors.Count > 0)
                throw new ArgumentException($"{entry.Project.Name}: " + string.Join(" ", info.Errors));
        }
        return new WorkspaceContract(entry.Project, entry.Project.Generate(), entry.Template, info);
    }).ToList();
}

static void AddContract(ZipArchive zip, string folder, ContractProject project, GeneratedBundle bundle, string? templateBase64)
{
    void Add(string name, string content) => WriteEntry(zip, folder + name, content);
    Add("contract.ltrx.json", project.ToJson());
    Add("GeneratedTags.cs", bundle.ModelCs);
    Add("logixrelay.json", bundle.Configuration.ToJson());
    if (!project.IncludeImplicit) return;
    Add("RelayRoutine.L5X", bundle.RoutineL5x);
    Add("ModuleSetup.txt", bundle.ModuleInstructions);
    Add("LTR_SintMerge.L5X", bundle.MergeAoiL5x);
    Add("LTR_SintMerge.st", bundle.MergeStructuredText);
    if (templateBase64 is null) return;
    var module = ModuleTemplate.Generate(Convert.FromBase64String(templateBase64), bundle.Configuration, project.ModuleName, project.PcAddress);
    Add("GenericEthernetModule.L5X", module.Xml);
    Add("ModuleProvenance.txt", $"Native template SHA-256: {module.SourceSha256}\nGenerated as an offline review copy. Studio 5000 import/verify/re-export acceptance is still required.\n");
}

static void WriteEntry(ZipArchive zip, string name, string content)
{
    using var writer = new StreamWriter(zip.CreateEntry(name).Open(), new UTF8Encoding(false));
    writer.Write(content);
}

sealed record WorkspaceModuleTemplate(string Filename, string Base64);
sealed record WorkspaceContractRequest(JsonElement Project, WorkspaceModuleTemplate? ModuleTemplate);
sealed record WorkspaceRequest(int WorkspaceVersion, List<WorkspaceContractRequest> Contracts);
sealed record WorkspaceContract(ContractProject Project, GeneratedBundle Bundle, WorkspaceModuleTemplate? Template, ModuleTemplateInfo? TemplateInfo);
public sealed record OnlineRequest(string Gateway, string Route = "1,0", HeartbeatOptions? Heartbeat = null);
public sealed record GenerateRequest(TagCatalog Catalog, List<TagSelection> Selections, bool Implicit = true, string ModuleName = "LogixRelay", string SoftwareRevision = "",
    int AssemblyBytes = 0, string? ModuleTemplateBase64 = null, string PcAddress = "192.168.1.200", ExplicitOptions? Explicit = null, ImplicitOptions? AssemblyOptions = null, string ProjectName = "Contract", string Namespace = "LogixTagRelay.Generated", string ClassName = "GeneratedTags", string Gateway = "192.168.1.10", string Route = "1,0", HeartbeatOptions? Heartbeat = null);
