using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
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
            string page = await http.GetStringAsync("/", ct);
            Check(page.Contains("explicit-contracts") && page.Contains("implicit-contracts") && page.Contains("project-save-bottom") &&
                page.Contains("back-bottom") && page.Contains("interface-download"),
                "contract workspace and editor controls are served");
            http.DefaultRequestHeaders.Add("X-LogixTagRelay", "browser");
            const string source = """
                <RSLogix5000Content SoftwareRevision="35.03"><Controller Name="BrowserDemo"><Tags>
                <Tag Name="Double" TagType="Base" DataType="LREAL" ExternalAccess="Read/Write"/>
                <Tag Name="ShortTime" TagType="Base" DataType="TIME32" ExternalAccess="Read/Write"/>
                <Tag Name="Nano" TagType="Base" DataType="LTIME" ExternalAccess="Read/Write"/>
                <Tag Name="NanoDate" TagType="Base" DataType="LDT" ExternalAccess="Read/Write"/>
                <Tag Name="ApiAlive" TagType="Base" DataType="BOOL" ExternalAccess="Read/Write"/>
                <Tag Name="Timer1" TagType="Base" DataType="TIMER" ExternalAccess="Read/Write"/>
                </Tags></Controller></RSLogix5000Content>
                """;
            using var form = new MultipartFormDataContent();
            form.Add(new StringContent(source), "file", "demo.L5X");
            using var catalogResponse = await http.PostAsync("api/catalog", form, ct); catalogResponse.EnsureSuccessStatusCode();
            var catalog = JsonSerializer.Deserialize<TagCatalog>(await catalogResponse.Content.ReadAsStringAsync(ct), SyncConfiguration.Json)!;
            Check(catalog.Tags.All(t => t.Selectable), "remaining common types and timer members browse through HTTP");
            var project = new ContractProject("Browser", catalog.Tags.Where(t => t.Name != "ApiAlive").Select(t => new TagSelection(t.Path, Direction.Bidirectional)).ToList(), catalog,
                Namespace: "Browser.Contracts", ClassName: "BrowserTags", Implicit: new(111, 110, 112), Heartbeat: new("ApiAlive"));
            async Task<HttpResponseMessage> Post(string route, string json) => await http.PostAsync(route, new StringContent(json, Encoding.UTF8, "application/json"), ct);
            using var saved = await Post("api/project", project.ToJson());
            Check(saved.IsSuccessStatusCode, "portable project validates and reopens");
            var linked = project with
            {
                Catalog = null, SourceL5x = "demo.L5X",
                SourceSha256 = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source)))
            };
            async Task<HttpResponseMessage> Import(ContractProject linkedProject)
            {
                using var upload = new MultipartFormDataContent();
                upload.Add(new StringContent(linkedProject.ToJson()), "project");
                upload.Add(new StringContent(source), "file", "demo.L5X");
                return await http.PostAsync("api/project", upload, ct);
            }
            using var imported = await Import(linked);
            imported.EnsureSuccessStatusCode();
            using var importedJson = JsonDocument.Parse(await imported.Content.ReadAsStringAsync(ct));
            var portable = ContractProject.FromJson(importedJson.RootElement.GetProperty("project").GetRawText());
            Check(portable.SourceL5x is null && portable.Catalog is not null &&
                portable.Generate().Configuration.SchemaHash == project.Generate().Configuration.SchemaHash,
                "linked project imports with its uploaded L5X and retains its schema");
            using var wrongSource = await Import(linked with { SourceSha256 = new string('0', 64) });
            Check(wrongSource.StatusCode == HttpStatusCode.BadRequest, "linked import enforces the source hash");
            var request = new GenerateRequest(catalog, project.Selections, AssemblyOptions: project.Implicit, Namespace: project.Namespace, ClassName: project.ClassName, Heartbeat: project.Heartbeat);
            using var generated = await Post("api/generate", JsonSerializer.Serialize(request, SyncConfiguration.Json)); generated.EnsureSuccessStatusCode();
            using var zip = new ZipArchive(new MemoryStream(await generated.Content.ReadAsByteArrayAsync(ct)));
            string Read(string name) { using var reader = new StreamReader(zip.GetEntry(name)!.Open()); return reader.ReadToEnd(); }
            var config = SyncConfiguration.FromJson(Read("logixrelay.json"));
            Check(Read("GeneratedTags.cs").Contains("class BrowserTags") && Read("GeneratedTags.cs").Contains("CreateConfiguration") && config.Implicit!.PlcInputAssembly == 111,
                "download preserves C# identity and custom assembly settings");
            Check(ContractProject.FromJson(Read("contract.ltrx.json")).Generate().Configuration.SchemaHash == config.SchemaHash && Read("RelayRoutine.L5X").Contains("ResyncNonce"), "download includes a regenerable project and recovery routine");
            Check(config.Heartbeat is { Tag: "ApiAlive", IntervalMs: 5000 } && config.Fields.All(f => f.Path != "ApiAlive"), "browser retains the five-second explicit heartbeat outside the implicit image");
            using var layout = JsonDocument.Parse(await http.GetStringAsync("api/image-layout", ct));
            var layoutRoot = layout.RootElement;
            int alignment = layoutRoot.GetProperty("alignment").GetInt32();
            int minimumBytes = layoutRoot.GetProperty("headerBytes").GetInt32() + config.Fields.Sum(field =>
                (layoutRoot.GetProperty("scalarBytes").GetProperty(field.Type.ToString()).GetInt32() + alignment - 1) / alignment * alignment);
            Check(minimumBytes == config.PlcToAppBytes && config.AppToPlcBytes == minimumBytes &&
                layoutRoot.GetProperty("maxBytes").GetInt32() == 480 &&
                layoutRoot.GetProperty("stringOverhead").GetInt32() + 17 == PlcTypes.Size(PlcType.STRING, 17), "image sizing metadata matches the generated layout and STRING codec");
            var explicitProject = project with { Name = "Explicit", ClassName = "ExplicitTags", IncludeImplicit = false, Implicit = null, Heartbeat = null };
            const string moduleSource = """
                <RSLogix5000Content SoftwareRevision="35.03" TargetName="Template" TargetType="Module"><Controller Name="Test"><Modules>
                <Module Use="Target" Name="Template" CatalogNumber="ETHERNET-MODULE"><Ports><Port Address="192.0.2.2" /></Ports>
                <Communications PrimCxnInputSize="480" PrimCxnOutputSize="480"><ConfigTag ConfigSize="0"><Data Format="L5K"><![CDATA[[5,112,[0,0]]]]></Data></ConfigTag>
                <Connections><Connection Name="Standard" InputCxnPoint="111" OutputCxnPoint="110" InputSize="480" OutputSize="480">
                <InputTag><Data Format="Decorated"><Structure DataType="AB:ETHERNET_MODULE_SINT_480Bytes:I:0"><ArrayMember Name="Data" DataType="SINT" Dimensions="480" /></Structure></Data></InputTag>
                <OutputTag><Data Format="Decorated"><Structure DataType="AB:ETHERNET_MODULE_SINT_480Bytes:O:0"><ArrayMember Name="Data" DataType="SINT" Dimensions="480" /></Structure></Data></OutputTag>
                </Connection></Connections></Communications></Module></Modules></Controller></RSLogix5000Content>
                """;
            var template = new WorkspaceModuleTemplate("Template.L5X", Convert.ToBase64String(Encoding.UTF8.GetBytes(moduleSource)));
            string Workspace(params ContractProject[] projects) => JsonSerializer.Serialize(new WorkspaceRequest(1, projects.Select(p =>
                new WorkspaceContractRequest(JsonSerializer.SerializeToElement(p, SyncConfiguration.Json), p.IncludeImplicit ? template : null)).ToList()), SyncConfiguration.Json);
            string workspace = Workspace(explicitProject, explicitProject with { Name = "Second explicit" }, project);
            using var workspaceDownload = await Post("api/workspace/generate", workspace);
            workspaceDownload.EnsureSuccessStatusCode();
            using var archive = new ZipArchive(new MemoryStream(await workspaceDownload.Content.ReadAsByteArrayAsync(ct)));
            Check(archive.Entries.Count(e => e.Name == "GeneratedTags.cs") == 3 && archive.Entries.Count(e => e.Name == "logixrelay.json") == 3 &&
                archive.Entries.Count(e => e.Name == "RelayRoutine.L5X") == 1 && archive.GetEntry("contracts/03-BrowserTags/GenericEthernetModule.L5X") is not null,
                "one interface archive contains every contract, the PLC routine and the attached module import");
            using var workspaceFile = new StreamReader(archive.GetEntry("LogixTagRelay.workspace.json")!.Open());
            using var reopened = await Post("api/workspace", await workspaceFile.ReadToEndAsync(ct));
            reopened.EnsureSuccessStatusCode();
            using var reopenedJson = JsonDocument.Parse(await reopened.Content.ReadAsStringAsync(ct));
            Check(reopenedJson.RootElement.GetArrayLength() == 3 && reopenedJson.RootElement[2].GetProperty("template").GetProperty("base64").GetString() == template.Base64,
                "the project in the archive reopens all contracts and restores the module attachment");
            using var secondImplicit = await Post("api/workspace/generate", Workspace(project, project));
            Check(secondImplicit.StatusCode == HttpStatusCode.BadRequest, "a workspace rejects a second implicit contract");
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
