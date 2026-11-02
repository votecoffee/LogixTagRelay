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
  & dotnet run --project samples/StringsAndArraysDemo -c Release --no-build
  if ($LASTEXITCODE) { throw 'STRING/array consumer failed.' }
  & dotnet run --project samples/BidirectionalDemo -c Release --no-build
  if ($LASTEXITCODE) { throw 'Bidirectional UDT consumer failed.' }
  & dotnet run --project tools/LogixTagRelay.Emulator -c Release --no-build -- --self-test true
  if ($LASTEXITCODE) { throw 'Emulator HTTP checks failed.' }
  & dotnet run --project samples/TypesAndEventsDemo -c Release --no-build
  if ($LASTEXITCODE) { throw 'Types/events explicit consumer failed.' }
  & dotnet run --project samples/TypesAndEventsDemo -c Release --no-build -- --implicit
  if ($LASTEXITCODE) { throw 'Types/events implicit consumer failed.' }
  & "$PSScriptRoot/pack.ps1"
} finally { Pop-Location }
