using System.Text;
using System.Text.Json;
using LogixTagRelay;
using LogixTagRelay.Design;

try
{
    if (args is ["generate", var projectFile, var directory])
    {
        var project = ContractProject.FromJson(File.ReadAllText(projectFile));
        var bundle = project.Generate(Path.GetDirectoryName(Path.GetFullPath(projectFile)));
        Directory.CreateDirectory(directory);
        void Write(string name, string content)
        {
            string path = Path.Combine(directory, name);
            if (!File.Exists(path) || File.ReadAllText(path) != content) File.WriteAllText(path, content, new UTF8Encoding(false));
        }
        Write("GeneratedTags.g.cs", bundle.ModelCs); Write("logixrelay.json", bundle.Configuration.ToJson());
        if (project.IncludeImplicit)
        {
            Write("RelayRoutine.L5X", bundle.RoutineL5x); Write("ModuleSetup.txt", bundle.ModuleInstructions);
            Write("LTR_SintMerge.L5X", bundle.MergeAoiL5x); Write("LTR_SintMerge.st", bundle.MergeStructuredText);
        }
    }
    else if (args is ["compare", var oldFile, var newFile])
    {
        var before = SyncConfiguration.FromJson(File.ReadAllText(oldFile));
        var after = SyncConfiguration.FromJson(File.ReadAllText(newFile));
        Console.WriteLine(JsonSerializer.Serialize(ContractComparison.Compare(before, after), SyncConfiguration.Json));
    }
    else throw new ArgumentException("Usage: generate <project.ltrx.json> <output-directory> | compare <old-config.json> <new-config.json>");
    return 0;
}
catch (Exception error) when (error is ArgumentException or FormatException or IOException or System.Xml.XmlException or JsonException)
{
    string path = args.Length > 1 ? Path.GetFullPath(args[1]) : "LogixTagRelay";
    Console.Error.WriteLine($"{path}(1,1): error LTR001: {error.Message.Replace('\r', ' ').Replace('\n', ' ')}");
    return 1;
}
