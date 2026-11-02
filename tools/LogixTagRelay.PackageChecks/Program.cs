using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Compression;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

string[] libraries = ["LogixTagRelay", "LogixTagRelay.Design", "LogixTagRelay.Logix", "LogixTagRelay.Opener", "LogixTagRelay.Emulation"];
try
{
    if (args.Length is < 2 or > 4) throw new ArgumentException("Usage: <package-directory> <version> [expected-commit] [source-repository]");
    string directory = Path.GetFullPath(args[0]), version = args[1];
    var commits = new HashSet<string>(StringComparer.Ordinal);
    var receipts = new List<object>();
    foreach (string id in libraries.Append("LogixTagRelay.Build"))
    {
        string file = Path.Combine(directory, $"{id}.{version}.nupkg");
        using var zip = ZipFile.OpenRead(file);
        var entries = zip.Entries.Select(e => e.FullName).ToHashSet(StringComparer.Ordinal);
        Require(entries.Count == zip.Entries.Count, "Duplicate package entries: " + id);
        Require(entries.All(e => !e.Contains('\\') && !e.StartsWith('/') && !e.Split('/').Contains("..")), "Invalid package paths: " + id);
        var xml = Xml(Read(zip, id + ".nuspec"));
        XNamespace ns = xml.Root!.Name.Namespace;
        var metadata = xml.Root.Element(ns + "metadata")!;
        Require(metadata.Element(ns + "id")?.Value == id && metadata.Element(ns + "version")?.Value == version, "Package identity mismatch: " + id);
        var repository = metadata.Element(ns + "repository");
        string commit = repository?.Attribute("commit")?.Value ?? "";
        Require(repository?.Attribute("url")?.Value == "https://github.com/votecoffee/LogixTagRelay" && Regex.IsMatch(commit, "^[a-f0-9]{40}$"), "Missing repository provenance: " + id);
        if (args.Length >= 3) Require(commit == args[2], "Unexpected source commit: " + id);
        commits.Add(commit);
        Require(metadata.Element(ns + "readme")?.Value == "README.md" && entries.Contains("README.md"), "Missing README: " + id);
        Require(metadata.Element(ns + "license")?.Value == "LICENSE" && entries.Contains("LICENSE"), "Missing license: " + id);
        Require(!metadata.Descendants(ns + "dependency").Any(d => (d.Attribute("id")?.Value ?? "").Contains("PublicApiAnalyzers", StringComparison.OrdinalIgnoreCase)), "Build analyzer leaked into runtime dependencies: " + id);
        int documents;
        if (id == "LogixTagRelay.Build")
        {
            const string prefix = "tools/net8.0/any/";
            Require(entries.Contains("buildTransitive/LogixTagRelay.Build.targets"), "Missing generator build target.");
            foreach (string dependency in new[] { "LogixTagRelay", "LogixTagRelay.Design" }) CheckXml(zip, prefix + dependency + ".xml");
            using var pe = new PEReader(new MemoryStream(Read(zip, prefix + id + ".dll")));
            var debug = pe.ReadDebugDirectory().Single(d => d.Type == DebugDirectoryEntryType.EmbeddedPortablePdb);
            using var pdb = pe.ReadEmbeddedPortablePdbDebugDirectoryData(debug);
            documents = CheckPdb(pdb.GetMetadataReader(), commit, args.Length == 4 ? args[3] : null);
        }
        else
        {
            string prefix = "lib/net8.0/" + id;
            CheckXml(zip, prefix + ".xml");
            string symbols = Path.Combine(directory, $"{id}.{version}.snupkg");
            using var symbolZip = ZipFile.OpenRead(symbols);
            using var pdb = MetadataReaderProvider.FromPortablePdbStream(new MemoryStream(Read(symbolZip, prefix + ".pdb")));
            var reader = pdb.GetMetadataReader();
            using var pe = new PEReader(new MemoryStream(Read(zip, prefix + ".dll")));
            var codeViewEntry = pe.ReadDebugDirectory().Single(e => e.Type == DebugDirectoryEntryType.CodeView);
            var codeView = pe.ReadCodeViewDebugDirectoryData(codeViewEntry);
            byte[] identity = reader.DebugMetadataHeader!.Id.ToArray();
            Require(codeView.Guid == new Guid(identity.AsSpan(0, 16)) && codeViewEntry.Stamp == BinaryPrimitives.ReadUInt32LittleEndian(identity.AsSpan(16)), "PDB does not match DLL: " + id);
            Require(codeView.Age == 1, "Expected a portable PDB identity: " + id);
            documents = CheckPdb(reader, commit, args.Length == 4 ? args[3] : null);
            receipts.Add(Receipt(symbols));
        }
        if (id == "LogixTagRelay.Opener")
        {
            using var native = new PEReader(new MemoryStream(Read(zip, "runtimes/win-x64/native/LogixTagRelayOpener.dll")));
            Require(native.PEHeaders.CoffHeader.Machine == Machine.Amd64, "Native bridge is not Windows x64.");
            Require(entries.Contains("licenses/OpENer.txt") && entries.Contains("licenses/THIRD_PARTY_NOTICES.md"), "Missing native license notices.");
        }
        receipts.Add(Receipt(file));
        Console.WriteLine($"PASS {id}: provenance, XML, symbols and Source Link ({documents} source documents)");
    }
    Require(commits.Count == 1, "Packages were built from different source commits.");
    File.WriteAllText(Path.Combine(directory, $"release-manifest-{version}.json"), JsonSerializer.Serialize(new
    {
        version, sourceCommit = commits.Single(), sourceChecksumsVerified = args.Length == 4, packages = receipts,
        libraries = libraries.Length, generatorSymbols = "embedded portable PDB",
        liveControllerAcceptance = false
    }, new JsonSerializerOptions { WriteIndented = true }) + "\n");
    Console.WriteLine("PASS 6 NuGets, 5 symbol packages, embedded generator symbols and release manifest.");
    return 0;
}
catch (Exception error)
{
    Console.Error.WriteLine("Package validation failed: " + error.Message);
    return 1;
}

static void Require(bool value, string message) { if (!value) throw new InvalidDataException(message); }
static byte[] Read(ZipArchive zip, string path)
{
    using var stream = (zip.GetEntry(path) ?? throw new InvalidDataException("Missing package entry: " + path)).Open();
    using var copy = new MemoryStream(); stream.CopyTo(copy); return copy.ToArray();
}
static XDocument Xml(byte[] content)
{
    using var input = new MemoryStream(content);
    using var reader = XmlReader.Create(input, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
    return XDocument.Load(reader);
}
static void CheckXml(ZipArchive zip, string path)
{
    var members = Xml(Read(zip, path)).Root?.Element("members")?.Elements("member").ToArray() ?? [];
    // Regex source generators also emit XML remarks for private helper methods.
    // CS1591 enforces public source documentation; every distributed entry must contain readable documentation.
    Require(members.Length > 0 && members.All(m => !string.IsNullOrWhiteSpace(m.Value) || m.Element("inheritdoc") is not null), "Missing API documentation: " + path);
    Require(members.All(m => !m.Value.Contains("MISSING", StringComparison.Ordinal)), "Incomplete API documentation: " + path);
}
static int CheckPdb(MetadataReader reader, string commit, string? repository)
{
    Guid sourceLink = new("cc110556-a091-4d38-9fec-25ab9a351a6a");
    var links = reader.CustomDebugInformation.Select(h => reader.GetCustomDebugInformation(h))
        .Where(i => reader.GetGuid(i.Kind) == sourceLink).ToArray();
    Require(links.Length == 1, "Missing or ambiguous Source Link metadata.");
    using var json = JsonDocument.Parse(reader.GetBlobBytes(links[0].Value));
    var mappings = json.RootElement.GetProperty("documents").EnumerateObject().ToArray();
    string prefix = "https://raw.githubusercontent.com/votecoffee/LogixTagRelay/" + commit + "/";
    const string nativeSource = "https://raw.githubusercontent.com/EIPStackGroup/OpENer/0e1ab7ee5c272950c0dc8daf7793008d7a518912/*";
    Require(mappings.Any(m => m.Name == "/_/*" && m.Value.GetString() == prefix + "*") &&
        mappings.All(m => (m.Name == "/_/*" && m.Value.GetString() == prefix + "*") ||
            (m.Name == "/_/native/OpENer/*" && m.Value.GetString() == nativeSource)), "Source Link is not pinned to the package or native submodule commit.");
    foreach (var handle in reader.Documents)
    {
        var document = reader.GetDocument(handle);
        string name = reader.GetString(document.Name);
        Require(name.StartsWith("/_/", StringComparison.Ordinal), "Nonportable source path in PDB: " + name);
        Require(!document.Hash.IsNil && !document.HashAlgorithm.IsNil, "Missing document checksum: " + name);
        if (repository is not null)
        {
            Guid embeddedSource = new("0e8a571b-6926-466e-b4ad-8ab04611f5fe");
            var embedded = reader.GetCustomDebugInformation(handle).Select(h => reader.GetCustomDebugInformation(h))
                .Where(i => reader.GetGuid(i.Kind) == embeddedSource).ToArray();
            byte[] content;
            if (embedded.Length == 1)
            {
                byte[] data = reader.GetBlobBytes(embedded[0].Value);
                int length = BinaryPrimitives.ReadInt32LittleEndian(data);
                if (length == 0) content = data[4..];
                else
                {
                    using var compressed = new DeflateStream(new MemoryStream(data, 4, data.Length - 4), CompressionMode.Decompress);
                    using var decoded = new MemoryStream(); compressed.CopyTo(decoded); content = decoded.ToArray();
                    Require(content.Length == length, "Invalid embedded source length: " + name);
                }
            }
            else
            {
                Require(embedded.Length == 0, "Ambiguous embedded source: " + name);
                var start = new ProcessStartInfo("git") { WorkingDirectory = Path.GetFullPath(repository), RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
                start.ArgumentList.Add("show"); start.ArgumentList.Add(commit + ":" + name[3..]);
                using var git = Process.Start(start) ?? throw new IOException("Cannot read committed source.");
                var error = git.StandardError.ReadToEndAsync();
                using var bytes = new MemoryStream(); git.StandardOutput.BaseStream.CopyTo(bytes); git.WaitForExit();
                Require(git.ExitCode == 0, "Source unavailable at the pinned commit: " + name + " " + error.GetAwaiter().GetResult());
                content = bytes.ToArray();
            }
            Guid algorithm = reader.GetGuid(document.HashAlgorithm);
            // SDK regex generators can supply SHA-1 SourceText even when ordinary source files use SHA-256.
            // Honor the declared PDB algorithm and verify the actual bytes in either case.
            byte[] hash = algorithm == new Guid("8829d00f-11b8-4213-878b-770e8597ac16") ? SHA256.HashData(content)
                : algorithm == new Guid("ff1816ec-aa5e-4d10-87f7-6f4963833460") ? SHA1.HashData(content)
                : throw new InvalidDataException("Unsupported source checksum algorithm: " + name);
            Require(hash.SequenceEqual(reader.GetBlobBytes(document.Hash)), "Source checksum differs from its committed or embedded bytes: " + name);
        }
    }
    Require(reader.Documents.Count > 0, "Empty source document table.");
    return reader.Documents.Count;
}
static object Receipt(string path) => new { file = Path.GetFileName(path), sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant() };
