$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
Push-Location $repoRoot
try {
  & "$PSScriptRoot/build-native.ps1"
  & dotnet build -c Release
  if ($LASTEXITCODE) { throw 'Managed build failed.' }
  & dotnet run --project tests/LogixTagRelay.Tests -c Release --no-build
  if ($LASTEXITCODE) { throw 'Offline checks failed.' }
  & dotnet run --project samples/GeneratedDemo -c Release --no-build
  if ($LASTEXITCODE) { throw 'Generated consumer failed.' }
  & "$PSScriptRoot/pack.ps1"
} finally { Pop-Location }
