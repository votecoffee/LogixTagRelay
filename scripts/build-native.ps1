param([string]$Configuration = 'Release')
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$nativeBuild = Join-Path $repoRoot 'artifacts/native'
& git -C $repoRoot submodule update --init native/OpENer
if ($LASTEXITCODE) { throw 'Submodule initialization failed.' }
& cmake -S (Join-Path $repoRoot 'native') -B $nativeBuild -G 'Visual Studio 17 2022' -A x64
if ($LASTEXITCODE) { throw 'CMake configuration failed.' }
& cmake --build $nativeBuild --config $Configuration --target LogixTagRelayOpener --parallel 4
if ($LASTEXITCODE) { throw 'Native build failed.' }
Write-Output (Join-Path $nativeBuild "$Configuration/LogixTagRelayOpener.dll")
