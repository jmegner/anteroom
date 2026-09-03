<#
    Publishes Anteroom.exe and anteroom-hook.exe side by side into .\dist.

    Both are framework-dependent (they need the .NET 7 Desktop Runtime, which this machine has).
    Add -SelfContained to produce a build that carries the runtime with it.
#>
param(
    [string]$Output = "$PSScriptRoot\dist",
    [switch]$SelfContained,

    # Stamped into both binaries. CI passes the release tag; locally you can leave it off.
    [string]$Version
)

$ErrorActionPreference = 'Stop'

$common = @('-c', 'Release', '-o', $Output)
if ($SelfContained) {
    $common += @('-r', 'win-x64', '--self-contained', 'true')
}
if ($Version) {
    $common += "-p:Version=$Version"
}

Write-Host "Publishing Anteroom to $Output" -ForegroundColor Cyan

# The shim goes first; publishing the app afterwards must not overwrite it.
& dotnet publish "$PSScriptRoot\src\Anteroom.Hook\Anteroom.Hook.csproj" @common
if ($LASTEXITCODE -ne 0) { throw 'Publishing the hook shim failed.' }

& dotnet publish "$PSScriptRoot\src\Anteroom.App\Anteroom.App.csproj" @common
if ($LASTEXITCODE -ne 0) { throw 'Publishing the app failed.' }

$app = Join-Path $Output 'Anteroom.exe'
$hook = Join-Path $Output 'anteroom-hook.exe'

foreach ($file in @($app, $hook)) {
    if (-not (Test-Path $file)) { throw "Expected $file in the output but it is missing." }
}

Write-Host ''
Write-Host 'Done.' -ForegroundColor Green
Write-Host "  App:  $app"
Write-Host "  Hook: $hook"
Write-Host ''
Write-Host 'Run Anteroom.exe, then use Advanced settings > Connect to Claude Code.'
Write-Host 'Moving this folder later means reconnecting, since the hook path is absolute.'
