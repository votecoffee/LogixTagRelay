using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json;
using LogixTagRelay;
using LogixTagRelay.Design;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;

sealed class BrowserSelfTest(IServer server, IHostApplicationLifetime lifetime) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = lifetime.ApplicationStarted.Register(() => started.TrySetResult()); await started.Task.WaitAsync(stoppingToken);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20)); var ct = timeout.Token;
        int count = 0;
        void Check(bool ok, string name) { if (!ok) throw new Exception(name); count++; Console.WriteLine("PASS browser HTTP " + name); }
        try
        {
            using var http = new HttpClient { BaseAddress = new(server.Features.Get<IServerAddressesFeature>()!.Addresses.Single()) };
            Check((await http.GetStringAsync("/", ct)).Contains("project-save"), "project controls are served");
            http.DefaultRequestHeaders.Add("X-LogixTagRelay", "browser");
            using var form = new MultipartFormDataContent();
            form.Add(new StringContent("""
                <RSLogix5000Content SoftwareRevision="35.03"><Controller Name="BrowserDemo"><Tags>
                <Tag Name="Double" TagType="Base" DataType="LREAL" ExternalAccess="Read/Write"/>
                <Tag Name="ShortTime" TagType="Base" DataType="TIME32" ExternalAccess="Read/Write"/>
                <Tag Name="Nano" TagType="Base" DataType="LTIME" ExternalAccess="Read/Write"/>
                <Tag Name="NanoDate" TagType="Base" DataType="LDT" ExternalAccess="Read/Write"/>
                <Tag Name="ApiAlive" TagType="Base" DataType="BOOL" ExternalAccess="Read/Write"/>
                <Tag Name="Timer1" TagType="Base" DataType="TIMER" ExternalAccess="Read/Write"/>
                </Tags></Controller></RSLogix5000Content>
                """), "file", "demo.L5X");
            using var catalogResponse = await http.PostAsync("api/catalog", form, ct); catalogResponse.EnsureSuccessStatusCode();
            var catalog = JsonSerializer.Deserialize<TagCatalog>(await catalogResponse.Content.ReadAsStringAsync(ct), SyncConfiguration.Json)!;
            Check(catalog.Tags.All(t => t.Selectable), "remaining common types and timer members browse through HTTP");
            var project = new ContractProject("Browser", catalog.Tags.Where(t => t.Name != "ApiAlive").Select(t => new TagSelection(t.Path, Direction.Bidirectional)).ToList(), catalog,
                Namespace: "Browser.Contracts", ClassName: "BrowserTags", Implicit: new(111, 110, 112), Heartbeat: new("ApiAlive"));
            async Task<HttpResponseMessage> Post(string route, string json) => await http.PostAsync(route, new StringContent(json, Encoding.UTF8, "application/json"), ct);
            using var saved = await Post("api/project", project.ToJson());
            Check(saved.IsSuccessStatusCode, "portable project validates and reopens");
            var request = new GenerateRequest(catalog, project.Selections, AssemblyOptions: project.Implicit, Namespace: project.Namespace, ClassName: project.ClassName, Heartbeat: project.Heartbeat);
            using var generated = await Post("api/generate", JsonSerializer.Serialize(request, SyncConfiguration.Json)); generated.EnsureSuccessStatusCode();
            using var zip = new ZipArchive(new MemoryStream(await generated.Content.ReadAsByteArrayAsync(ct)));
            string Read(string name) { using var reader = new StreamReader(zip.GetEntry(name)!.Open()); return reader.ReadToEnd(); }
            var config = SyncConfiguration.FromJson(Read("logixrelay.json"));
            Check(Read("GeneratedTags.cs").Contains("class BrowserTags") && Read("GeneratedTags.cs").Contains("CreateConfiguration") && config.Implicit!.PlcInputAssembly == 111,
                "download preserves C# identity and custom assembly settings");
            Check(ContractProject.FromJson(Read("contract.ltrx.json")).Generate().Configuration.SchemaHash == config.SchemaHash && Read("RelayRoutine.L5X").Contains("ResyncNonce"), "download includes a regenerable project and recovery routine");
            Check(config.Heartbeat is { Tag: "ApiAlive", IntervalMs: 5000 } && config.Fields.All(f => f.Path != "ApiAlive"), "browser retains the five-second explicit heartbeat outside the implicit image");
            using var bad = await Post("api/project", (project with { Namespace = "namespace" }).ToJson());
            Check(bad.StatusCode == HttpStatusCode.BadRequest, "invalid C# identity is a reviewable validation error");
            using var duplicate = await Post("api/project", project.ToJson().Replace("\"version\": 1", "\"version\": 1, \"version\": 1"));
            Check(duplicate.StatusCode == HttpStatusCode.BadRequest, "duplicate project JSON keys are rejected");
            using var large = await Post("api/generate", JsonSerializer.Serialize(request with { AssemblyBytes = 484 }, SyncConfiguration.Json));
            Check(large.StatusCode == HttpStatusCode.BadRequest, "browser never emits oversized implicit images");
            Console.WriteLine($"{count} browser HTTP checks passed.");
        }
        catch (Exception error) { Console.Error.WriteLine(error); Environment.ExitCode = 1; }
        finally { lifetime.StopApplication(); }
    }
}
