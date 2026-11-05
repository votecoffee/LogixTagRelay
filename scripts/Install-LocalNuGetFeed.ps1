param(
    [string]$PackageDirectory,
    [string]$FeedPath = (Join-Path $env:LOCALAPPDATA 'LogixTagRelay\NuGetFeed')
)

$ErrorActionPreference = 'Stop'
$packagePattern = '^LogixTagRelay(?:\.(?:Build|Design|Emulation|Logix|Opener))?\.\d.*\.nupkg$'
if (!$PackageDirectory) {
    $PackageDirectory = $PSScriptRoot
    if (!(Get-ChildItem -LiteralPath $PackageDirectory -File | Where-Object Name -Match $packagePattern)) {
        $PackageDirectory = Join-Path (Split-Path -Parent $PSScriptRoot) 'artifacts/packages'
    }
}
$packages = @(Get-ChildItem -LiteralPath $PackageDirectory -File | Where-Object Name -Match $packagePattern)
if (!$packages.Count) { throw "No LogixTagRelay .nupkg files were found in $PackageDirectory." }

$destination = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($FeedPath)
foreach ($package in $packages) {
    $target = Join-Path $destination $package.Name
    if (Test-Path -LiteralPath $target) {
        if ((Get-FileHash -LiteralPath $target).Hash -ne (Get-FileHash -LiteralPath $package.FullName).Hash) {
            throw "The feed already contains different bytes for $($package.Name). Use a different feed folder or a new package version."
        }
    }
}
[IO.Directory]::CreateDirectory($destination) | Out-Null
foreach ($package in $packages) {
    $target = Join-Path $destination $package.Name
    if (!(Test-Path -LiteralPath $target)) { [IO.File]::Copy($package.FullName, $target) }
}
Write-Host "Local NuGet feed: $destination"
Write-Host 'In Visual Studio, add this folder under Tools > Options > NuGet Package Manager > Package Sources.'
