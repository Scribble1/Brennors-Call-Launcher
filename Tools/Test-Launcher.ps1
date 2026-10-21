param([switch]$SkipDesktopBuild)
$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot -Parent
dotnet build (Join-Path $root 'Tests/LauncherWorker') -c Release
if ($LASTEXITCODE -ne 0) { throw 'IPC test worker build failed.' }
dotnet run --project (Join-Path $root 'Tests/Launcher') -c Release -- $root
if ($LASTEXITCODE -ne 0) { throw 'Launcher checks failed.' }
dotnet build (Join-Path $root 'Launcher/ReleaseTool') -c Release
if ($LASTEXITCODE -ne 0) { throw 'Release tool build failed.' }
dotnet build (Join-Path $root 'Launcher/DistributionHost') -c Release
if ($LASTEXITCODE -ne 0) { throw 'Distribution host build failed.' }
if (!$SkipDesktopBuild) {
    dotnet build (Join-Path $root 'Launcher/Desktop') -c Release
    if ($LASTEXITCODE -ne 0) { throw 'Desktop build failed.' }
    dotnet build (Join-Path $root 'Launcher/UpdateHost') -c Release
    if ($LASTEXITCODE -ne 0) { throw 'Update host build failed.' }
    $preview=Join-Path $root '.local/launcher-preview.png'
    dotnet run --project (Join-Path $root 'Tests/LauncherUi') -c Release -- $preview (Join-Path $root '.local/launcher-preview-profile')
    if ($LASTEXITCODE -ne 0) { throw 'WPF startup/render check failed.' }
}
