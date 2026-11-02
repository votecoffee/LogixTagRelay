using System.IO.Compression;
using System.Text;
using System.Text.Json.Serialization;
using LogixTagRelay.Design;
using LogixTagRelay.Logix;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls("http://127.0.0.1:5274");
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
    catch (Exception error) when (error is ArgumentException or FormatException or System.Xml.XmlException or InvalidOperationException)
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
app.MapPost("/api/generate", (GenerateRequest request) =>
{
    var bundle = Generator.Generate(request.Catalog, request.Selections, request.Implicit, request.ModuleName, request.SoftwareRevision);
    using var bytes = new MemoryStream();
    using (var zip = new ZipArchive(bytes, ZipArchiveMode.Create, true))
    {
        void Add(string name, string content)
        { using var writer = new StreamWriter(zip.CreateEntry(name).Open(), new UTF8Encoding(false)); writer.Write(content); }
        Add("GeneratedTags.cs", bundle.ModelCs); Add("logixrelay.json", bundle.Configuration.ToJson());
        if (request.Implicit) { Add("RelayRoutine.L5X", bundle.RoutineL5x); Add("ModuleSetup.txt", bundle.ModuleInstructions); }
    }
    return Results.File(bytes.ToArray(), "application/zip", "LogixTagRelay.Generated.zip");
});
app.Run();
public sealed record OnlineRequest(string Gateway, string Route = "1,0");
public sealed record GenerateRequest(TagCatalog Catalog, List<TagSelection> Selections, bool Implicit = true, string ModuleName = "LogixRelay", string SoftwareRevision = "");
