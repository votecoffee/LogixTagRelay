param([string]$BaselineVersion)
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
foreach ($packageName in @('LogixTagRelay','LogixTagRelay.Design','LogixTagRelay.Logix','LogixTagRelay.Opener','LogixTagRelay.Emulation','LogixTagRelay.Build')) {
  & dotnet pack (Join-Path $repoRoot "src/$packageName/$packageName.csproj") -c Release --no-build -o (Join-Path $repoRoot 'artifacts/packages') "-p:LogixTagRelayBaselineVersion=$BaselineVersion"
  if ($LASTEXITCODE) { throw "Packaging $packageName failed." }
}
