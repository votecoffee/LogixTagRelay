using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using LogixTagRelay;

internal static class InterfaceBuild
{
    private const string AssemblyName = "LogixTagRelay.Contracts";
    private const string ProjectName = AssemblyName + ".csproj";

    public static async Task AddToArchiveAsync(ZipArchive archive, Dictionary<string, string> models, CancellationToken cancellationToken)
    {
        var runtime = typeof(SyncConfiguration).Assembly;
        var version = runtime.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion.Split('+')[0];
        var project = $"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net8.0</TargetFramework>
                <ImplicitUsings>enable</ImplicitUsings>
                <EnableDefaultCompileItems>false</EnableDefaultCompileItems>
                <GenerateDocumentationFile>true</GenerateDocumentationFile>
                <GenerateDependencyFile>false</GenerateDependencyFile>
                <DebugType>none</DebugType>
              </PropertyGroup>
              <ItemGroup>
                <Compile Include="contracts/*/GeneratedTags.cs" />
              </ItemGroup>
              <ItemGroup Condition="'$(RelayRuntimeAssembly)' == ''">
                <PackageReference Include="LogixTagRelay" Version="[{version}]" />
              </ItemGroup>
              <ItemGroup Condition="'$(RelayRuntimeAssembly)' != ''">
                <Reference Include="LogixTagRelay" HintPath="$(RelayRuntimeAssembly)" Private="false" />
              </ItemGroup>
            </Project>
            """;
        var root = Directory.CreateTempSubdirectory("LogixTagRelay-interface-").FullName;
        try
        {
            var projectPath = Path.Combine(root, ProjectName);
            await File.WriteAllTextAsync(projectPath, project, cancellationToken);
            foreach (var (name, source) in models)
            {
                string path = Path.Combine(root, name);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                await File.WriteAllTextAsync(path, source, cancellationToken);
            }

            var dist = Path.Combine(root, "dist");
            await BuildAsync(root, runtime.Location, dist, cancellationToken);
            foreach (var extension in new[] { ".dll", ".xml" })
            {
                var name = AssemblyName + extension;
                archive.CreateEntryFromFile(Path.Combine(dist, name), "dist/" + name);
            }
            archive.CreateEntryFromFile(projectPath, ProjectName);
            WriteText(archive, "Build Contracts.cmd", """
                @echo off
                setlocal
                dotnet build "%~dp0LogixTagRelay.Contracts.csproj" --configuration Release --output "%~dp0dist" --nologo ^
                  -p:ImportDirectoryBuildProps=false -p:ImportDirectoryBuildTargets=false -p:ImportDirectoryPackagesProps=false
                if errorlevel 1 (
                  echo Build failed. Check that the .NET SDK and the LogixTagRelay NuGet source are available.
                  pause
                  exit /b 1
                )
                echo Contract assembly rebuilt in "%~dp0dist".
                """.ReplaceLineEndings("\r\n") + "\r\n");
            WriteText(archive, "README.txt", $"""
                LogixTagRelay contract interface

                dist        Prebuilt .NET 8 contract DLL and IntelliSense XML.
                contracts   Editable .ltrx.json definitions, generated C# and per-contract JSON.
                plc         Studio 5000 files when the workspace includes implicit I/O.

                In Visual Studio, install the LogixTagRelay {version} NuGet and the driver you
                use, then add an assembly reference to dist\LogixTagRelay.Contracts.dll via Browse.
                See https://github.com/votecoffee/LogixTagRelay/blob/main/docs/CONSUMER_MANUAL.md
                for the package source and usage steps.

                Build Contracts.cmd recompiles the included C# using the installed .NET SDK
                and your configured NuGet sources. To change tag selections, reopen the workspace
                JSON in the editor and download again to regenerate matching C#, JSON and PLC files.
                """);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static async Task BuildAsync(string root, string runtimeAssembly, string dist, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
        {
            WorkingDirectory = root, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (string argument in new[] { "build", ProjectName, "--configuration", "Release", "--output", dist, "--nologo", "--disable-build-servers",
            $"-p:RelayRuntimeAssembly={runtimeAssembly}", $"-p:RestoreSources={root}", "-p:NuGetAudit=false",
            "-p:ImportDirectoryBuildProps=false", "-p:ImportDirectoryBuildTargets=false", "-p:ImportDirectoryPackagesProps=false" })
            start.ArgumentList.Add(argument);

        using var process = new Process { StartInfo = start };
        try { process.Start(); }
        catch (System.ComponentModel.Win32Exception error)
        { throw new InvalidOperationException("Install a .NET SDK with .NET 8 targeting support to compile the interface.", error); }
        var output = process.StandardOutput.ReadToEndAsync();
        var errorOutput = process.StandardError.ReadToEndAsync();
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(TimeSpan.FromSeconds(60));
        try { await process.WaitForExitAsync(limit.Token); }
        catch (OperationCanceledException)
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
            cancellationToken.ThrowIfCancellationRequested();
            throw new InvalidOperationException("The .NET interface build timed out after 60 seconds.");
        }
        var log = (await output + await errorOutput).Trim();
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"The contract library did not compile. {log[..Math.Min(log.Length, 2500)]}");
    }

    private static void WriteText(ZipArchive archive, string name, string content)
    {
        using var writer = new StreamWriter(archive.CreateEntry(name).Open());
        writer.WriteLine(content);
    }
}
