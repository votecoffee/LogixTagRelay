param([string]$PackageDirectory, [string]$Version)
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
if (!$PackageDirectory) { $PackageDirectory = Join-Path $repoRoot 'artifacts/packages' }
$PackageDirectory = (Resolve-Path -LiteralPath $PackageDirectory).Path
if (!$Version) { $Version = ([xml](Get-Content -Raw -LiteralPath (Join-Path $repoRoot 'Directory.Build.props'))).Project.PropertyGroup.Version }
$guardRoot = Join-Path ([IO.Path]::GetTempPath()) ('LogixTagRelay-guards-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $guardRoot | Out-Null
$utf8 = [Text.UTF8Encoding]::new($false)
function Write-Utf8([string]$Path, [string]$Text) { [IO.File]::WriteAllText($Path, $Text, $utf8) }
Write-Utf8 (Join-Path $guardRoot 'Directory.Build.props') '<Project />'
Write-Utf8 (Join-Path $guardRoot 'Directory.Build.targets') '<Project />'
Write-Utf8 (Join-Path $guardRoot 'NuGet.Config') '<configuration><packageSources><clear /><add key="nuget.org" value="https://api.nuget.org/v3/index.json" /></packageSources></configuration>'
Write-Utf8 (Join-Path $guardRoot 'Probe.csproj') @'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup><TargetFramework>net8.0</TargetFramework><Nullable>enable</Nullable><GenerateDocumentationFile>true</GenerateDocumentationFile><TreatWarningsAsErrors>true</TreatWarningsAsErrors></PropertyGroup>
  <ItemGroup><PackageReference Include="Microsoft.CodeAnalysis.PublicApiAnalyzers" Version="3.3.4" PrivateAssets="all" /><AdditionalFiles Include="PublicAPI.Shipped.txt;PublicAPI.Unshipped.txt" /></ItemGroup>
</Project>
'@
Write-Utf8 (Join-Path $guardRoot 'PublicAPI.Shipped.txt') "#nullable enable`nProbe.ApiProbe`nstatic Probe.ApiProbe.Name(string? path) -> string?`nstatic Probe.ApiProbe.Read(int timeout = 100) -> int`n"
Write-Utf8 (Join-Path $guardRoot 'PublicAPI.Unshipped.txt') "#nullable enable`n"
$original = @'
namespace Probe;
/// <summary>API compatibility probe.</summary>
public static class ApiProbe
{
    /// <summary>Original shipped member.</summary>
    public static int Read(int timeout = 100) => timeout;
    /// <summary>Nullable API contract.</summary>
    public static string? Name(string? path) => path;
}
'@
function Build-Probe([string]$Name, [string]$Source, [string]$ExpectedDiagnostic) {
  Write-Utf8 (Join-Path $guardRoot 'Probe.cs') $Source
  & dotnet build (Join-Path $guardRoot 'Probe.csproj') -c Release --nologo 2>&1 | Out-File -LiteralPath (Join-Path $guardRoot "$Name.log") -Encoding utf8
  $code = $LASTEXITCODE
  if ($ExpectedDiagnostic) {
    if ($code -eq 0 -or !(Select-String -LiteralPath (Join-Path $guardRoot "$Name.log") -Pattern "error $ExpectedDiagnostic" -Quiet)) { throw "Release guard did not reject $Name. See $guardRoot" }
  } elseif ($code -ne 0) { throw "Positive guard fixture failed. See $guardRoot" }
  Write-Output "PASS API/documentation guard: $Name"
}
Build-Probe 'valid-api' $original ''
Build-Probe 'removed-shipped-member' ($original.Replace('public static int Read(int timeout = 100) => timeout;', '')) 'RS0017'
Build-Probe 'unreviewed-api-addition' ($original.Replace('public static int Read(int timeout = 100) => timeout;', "public static int Read(int timeout = 100) => timeout;`n    /// <summary>Unreviewed new member.</summary>`n    public static void Write() { }")) 'RS0016'
Build-Probe 'changed-optional-default' ($original.Replace('int timeout = 100', 'int timeout = 200')) 'RS0016'
Build-Probe 'changed-nullability' ($original.Replace('Name(string? path)', 'Name(string path)')) 'RS0016'
Build-Probe 'undocumented-api' ($original.Replace('/// <summary>Original shipped member.</summary>', '')) 'CS1591'

$checker = Join-Path $repoRoot 'tools/LogixTagRelay.PackageChecks/bin/Release/net8.0/LogixTagRelay.PackageChecks.dll'
function Reject-Package([string]$Name, [string]$Id, [string]$Extension, [string]$Entry, [string]$ExpectedMessage, [byte[]]$Replacement) {
  $directory = Join-Path $guardRoot $Name
  New-Item -ItemType Directory -Path $directory | Out-Null
  foreach ($package in Get-ChildItem -LiteralPath $PackageDirectory -File | Where-Object { $_.Name.EndsWith(".$Version.nupkg") -or $_.Name.EndsWith(".$Version.snupkg") }) {
    Copy-Item -LiteralPath $package.FullName -Destination $directory
  }
  $archive = [IO.Compression.ZipFile]::Open((Join-Path $directory "$Id.$Version.$Extension"), [IO.Compression.ZipArchiveMode]::Update)
  try {
    $item = $archive.GetEntry($Entry)
    if (!$item) { throw "Guard fixture cannot find $Entry" }
    $item.Delete()
    if ($null -ne $Replacement) {
      $stream = $archive.CreateEntry($Entry).Open()
      try { $stream.Write($Replacement, 0, $Replacement.Length) } finally { $stream.Dispose() }
    }
  } finally { $archive.Dispose() }
  & dotnet $checker $directory $Version 2>&1 | Out-File -LiteralPath (Join-Path $guardRoot "$Name.log") -Encoding utf8
  if ($LASTEXITCODE -eq 0 -or !(Select-String -LiteralPath (Join-Path $guardRoot "$Name.log") -SimpleMatch -Pattern $ExpectedMessage -Quiet)) { throw "Package guard did not reject $Name. See $guardRoot" }
  Write-Output "PASS package guard: $Name"
}
Reject-Package 'missing-xml' 'LogixTagRelay' 'nupkg' 'lib/net8.0/LogixTagRelay.xml' 'Missing package entry' $null
$symbols = [IO.Compression.ZipFile]::OpenRead((Join-Path $PackageDirectory "LogixTagRelay.Design.$Version.snupkg"))
try {
  $stream = $symbols.GetEntry('lib/net8.0/LogixTagRelay.Design.pdb').Open()
  $bytes = [IO.MemoryStream]::new()
  try { $stream.CopyTo($bytes); $wrongPdb = $bytes.ToArray() } finally { $stream.Dispose(); $bytes.Dispose() }
} finally { $symbols.Dispose() }
Reject-Package 'mismatched-symbols' 'LogixTagRelay' 'snupkg' 'lib/net8.0/LogixTagRelay.pdb' 'PDB does not match DLL' $wrongPdb
Reject-Package 'missing-native-bridge' 'LogixTagRelay.Opener' 'nupkg' 'runtimes/win-x64/native/LogixTagRelayOpener.dll' 'Missing package entry' $null
Write-Output "PASS 9 release guard checks. Logs retained at $guardRoot"
