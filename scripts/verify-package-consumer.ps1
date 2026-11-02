param(
  [string]$PackageDirectory,
  [string]$Version,
  [ValidateSet('net8.0', 'net9.0')][string]$Framework = 'net8.0'
)
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
if (!$PackageDirectory) { $PackageDirectory = Join-Path $repoRoot 'artifacts/packages' }
$PackageDirectory = (Resolve-Path -LiteralPath $PackageDirectory).Path
if (!$Version) { $Version = ([xml](Get-Content -Raw -LiteralPath (Join-Path $repoRoot 'Directory.Build.props'))).Project.PropertyGroup.Version }
$packageIds = @('LogixTagRelay', 'LogixTagRelay.Build', 'LogixTagRelay.Design', 'LogixTagRelay.Logix', 'LogixTagRelay.Opener', 'LogixTagRelay.Emulation')
foreach ($id in $packageIds) {
  if (!(Test-Path -LiteralPath (Join-Path $PackageDirectory "$id.$Version.nupkg"))) { throw "Missing $id $Version in the selected package directory." }
}
$package = [IO.Compression.ZipFile]::OpenRead((Join-Path $PackageDirectory "LogixTagRelay.$Version.nupkg"))
try {
  $reader = [IO.StreamReader]::new($package.GetEntry('LogixTagRelay.nuspec').Open())
  try { $metadata = [xml]$reader.ReadToEnd() } finally { $reader.Dispose() }
  $sourceCommit = $metadata.SelectSingleNode("/*[local-name()='package']/*[local-name()='metadata']/*[local-name()='repository']/@commit").Value
  if ($sourceCommit -notmatch '^[a-f0-9]{40}$') { throw 'Selected package has no pinned source commit.' }
} finally { $package.Dispose() }
$runId = [Guid]::NewGuid().ToString('N')
$consumerRoot = Join-Path ([IO.Path]::GetTempPath()) "LogixTagRelay-consumer-$runId"
New-Item -ItemType Directory -Path $consumerRoot | Out-Null
foreach ($folder in @('Contracts', 'App')) { New-Item -ItemType Directory -Path (Join-Path $consumerRoot $folder) | Out-Null }
foreach ($file in @('Contracts/Contracts.csproj', 'Contracts/contract.ltrx.json', 'Contracts/Contract.L5X', 'App/App.csproj', 'App/Program.cs')) {
  Copy-Item -LiteralPath (Join-Path $repoRoot "tests/PackageConsumer/$file") -Destination (Join-Path $consumerRoot $file)
}
$utf8 = [Text.UTF8Encoding]::new($false)
function Write-Utf8([string]$Path, [string]$Text) { [IO.File]::WriteAllText($Path, $Text, $utf8) }
# Block ancestor build customization and all user/machine NuGet feeds.
Write-Utf8 (Join-Path $consumerRoot 'Directory.Build.props') '<Project />'
Write-Utf8 (Join-Path $consumerRoot 'Directory.Build.targets') '<Project />'
$escapedFeed = [Security.SecurityElement]::Escape($PackageDirectory)
Write-Utf8 (Join-Path $consumerRoot 'NuGet.Config') @"
<?xml version="1.0" encoding="utf-8"?>
<configuration><packageSources><clear /><add key="release" value="$escapedFeed" /><add key="nuget.org" value="https://api.nuget.org/v3/index.json" /></packageSources></configuration>
"@
$savedEnvironment = @{}
foreach ($name in @('NUGET_PACKAGES', 'NUGET_HTTP_CACHE_PATH', 'NUGET_PLUGINS_CACHE_PATH', 'DOTNET_CLI_HOME')) {
  $savedEnvironment[$name] = [Environment]::GetEnvironmentVariable($name, 'Process')
  [Environment]::SetEnvironmentVariable($name, (Join-Path $consumerRoot $name), 'Process')
}
function Invoke-ConsumerDotnet([string]$Log, [string[]]$Arguments, [switch]$ExpectFailure) {
  & dotnet @Arguments 2>&1 | Tee-Object -FilePath (Join-Path $consumerRoot $Log)
  $code = $LASTEXITCODE
  if ($ExpectFailure) {
    if ($code -eq 0) { throw "Expected failure succeeded: $Log" }
  } elseif ($code -ne 0) { throw "Consumer check failed ($code). See $consumerRoot/$Log" }
}
Push-Location $consumerRoot
try {
  $properties = @("-p:RelayPackageVersion=$Version", "-p:ConsumerFramework=$Framework")
  Invoke-ConsumerDotnet 'restore.log' (@('restore', 'App/App.csproj', '--configfile', 'NuGet.Config', '--no-cache', '--force', '-r', 'win-x64') + $properties)
  $assets = Get-Content -Raw -LiteralPath 'App/obj/project.assets.json' | ConvertFrom-Json
  $folders = @($assets.packageFolders.PSObject.Properties.Name)
  if ($folders.Count -ne 1 -or [IO.Path]::GetFullPath($folders[0]).TrimEnd([char[]]'\/') -ne $env:NUGET_PACKAGES.TrimEnd([char[]]'\/')) { throw 'Restore used an unexpected package cache.' }
  Invoke-ConsumerDotnet 'publish.log' (@('publish', 'App/App.csproj', '-c', 'Release', '-r', 'win-x64', '--self-contained', 'false', '--no-restore', '-o', 'publish') + $properties)
  Invoke-ConsumerDotnet 'run.log' @('publish/App.dll', $Version, $sourceCommit)
  if (!(Select-String -LiteralPath 'run.log' -Pattern '^PASS 18 package-consumer checks on' -Quiet)) { throw 'Consumer did not finish its complete check set.' }
  $contractDocs = @(Get-ChildItem -LiteralPath 'Contracts/bin/Release' -Recurse -Filter 'Contracts.xml')
  if ($contractDocs.Count -ne 1) { throw 'Expected one generated contract XML documentation file.' }
  $contractXml = [xml](Get-Content -Raw -LiteralPath $contractDocs[0].FullName)
  if (!($contractXml.doc.members.member.summary -match 'Machine position contract.')) { throw 'Source tag descriptions are missing from generated contract XML.' }
  $xmlText = [IO.File]::ReadAllText($contractDocs[0].FullName)
  if (!$xmlText.Contains('Requested &lt;position&gt; &amp; command.') -or !$xmlText.Contains('Measured position in millimetres.')) { throw 'Operand overrides or UDT member defaults are missing from compiled IntelliSense documentation.' }
  $contractPath = Join-Path $consumerRoot 'Contracts/contract.ltrx.json'
  $original = [IO.File]::ReadAllBytes($contractPath)
  try {
    Write-Utf8 $contractPath ([Text.Encoding]::UTF8.GetString($original).Replace('"ReleaseTags"', '"namespace"'))
    Invoke-ConsumerDotnet 'invalid-contract.log' (@('build', 'App/App.csproj', '-c', 'Release', '-r', 'win-x64', '--no-restore') + $properties) -ExpectFailure
    if (!(Select-String -LiteralPath 'invalid-contract.log' -Pattern 'error LTR001' -Quiet)) { throw 'Invalid contract did not produce LTR001.' }
  } finally { [IO.File]::WriteAllBytes($contractPath, $original) }
  $runtime = (Select-String -LiteralPath 'run.log' -Pattern '^PASS 18 package-consumer checks on').Line
  $receipt = [ordered]@{ version = $Version; sourceCommit = $sourceCommit; framework = $Framework; runtime = $runtime; packageChecks = 18; invalidContractRejected = $true; isolatedCache = $true; sourceProjectReferences = $false; nativeAbi = 2; liveControllerAcceptance = $false }
  Write-Utf8 (Join-Path $PackageDirectory "consumer-$Framework-$runId.json") (($receipt | ConvertTo-Json) + "`n")
  Write-Output "PASS isolated $Framework package consumer. Logs retained at $consumerRoot"
} finally {
  Pop-Location
  foreach ($name in $savedEnvironment.Keys) { [Environment]::SetEnvironmentVariable($name, $savedEnvironment[$name], 'Process') }
}
