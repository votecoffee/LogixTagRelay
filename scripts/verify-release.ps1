param([switch]$SkipNativeBuild, [string[]]$ConsumerFrameworks = @('net8.0'), [string]$BaselineVersion, [string]$BaselinePackageDirectory)
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
Push-Location $repoRoot
try {
  $status = & git status --porcelain --untracked-files=normal
  if ($LASTEXITCODE -or $status) { throw 'Release verification requires a clean committed source tree so Source Link resolves the packaged bytes.' }
  $commit = (& git rev-parse HEAD).Trim()
  if ($LASTEXITCODE) { throw 'Cannot determine source commit.' }
  $version = ([xml](Get-Content -Raw -LiteralPath 'Directory.Build.props')).Project.PropertyGroup.Version
  if (!$SkipNativeBuild) { & "$PSScriptRoot/build-native.ps1" }
  if ($BaselineVersion) {
    $sources = @('--source', 'https://api.nuget.org/v3/index.json')
    if ($BaselinePackageDirectory) { $sources += @('--source', (Resolve-Path -LiteralPath $BaselinePackageDirectory).Path) }
    & dotnet restore "-p:LogixTagRelayBaselineVersion=$BaselineVersion" @sources
    if ($LASTEXITCODE) { throw 'Cannot restore the selected prior package baseline.' }
  }
  & dotnet build -c Release -p:ContinuousIntegrationBuild=true "-p:LogixTagRelayBaselineVersion=$BaselineVersion"
  if ($LASTEXITCODE) { throw 'Release build or public API/XML documentation validation failed.' }
  & "$PSScriptRoot/pack.ps1" -BaselineVersion $BaselineVersion
  & dotnet run --project tools/LogixTagRelay.PackageChecks -c Release --no-build -- artifacts/packages $version $commit $repoRoot
  if ($LASTEXITCODE) { throw 'Release package contents, symbols or provenance validation failed.' }
  & "$PSScriptRoot/verify-release-guards.ps1" -Version $version
  foreach ($framework in $ConsumerFrameworks) {
    & "$PSScriptRoot/verify-package-consumer.ps1" -Version $version -Framework $framework
  }
  if (& git status --porcelain --untracked-files=normal) { throw 'Release verification modified tracked or unignored source.' }
  Write-Output "PASS release $version at $commit. No packages published and no PLC contacted."
} finally { Pop-Location }
