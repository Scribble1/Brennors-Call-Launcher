param(
    [string]$Version='0.2.0',
    [string]$DeploymentConfig='',
    [switch]$Installer,
    [string]$Iscc=''
)
$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot -Parent
if ($Version -notmatch '^\d+\.\d+\.\d+(\.\d+)?$') { throw 'Version must be numeric, for example 0.2.0.' }
$output=Join-Path $root "Artifacts/Launcher/$Version"
$desktop=Join-Path $output 'publish'
$helper=Join-Path $output 'helper'
dotnet publish (Join-Path $root 'Launcher/Desktop') -c Release -r win-x64 --self-contained true "-p:Version=$Version" -o $desktop
if ($LASTEXITCODE -ne 0) { throw 'Launcher publish failed.' }
dotnet publish (Join-Path $root 'Launcher/UpdateHost') -c Release -r win-x64 --self-contained true "-p:Version=$Version" -o $helper
if ($LASTEXITCODE -ne 0) { throw 'Update host publish failed.' }
Copy-Item -LiteralPath (Join-Path $helper 'BrennorsCall.UpdateHost.exe') -Destination $desktop -Force
if ($DeploymentConfig) {
    $publisher=Get-Content -LiteralPath $DeploymentConfig -Raw | ConvertFrom-Json
    if (!$publisher.trustedKeys.PSObject.Properties.Count -or !$publisher.channels.PSObject.Properties.Count) { throw 'Installer requires actual trusted keys and channel endpoints.' }
    Copy-Item -LiteralPath $DeploymentConfig -Destination (Join-Path $desktop 'deployment.json') -Force
}
if ($Installer) {
    if (!$DeploymentConfig) { throw 'Pass a publisher DeploymentConfig when creating an installer.' }
    if (!$Iscc) {
        $Iscc=@((Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6/ISCC.exe'),(Join-Path $env:ProgramFiles 'Inno Setup 7/ISCC.exe'),(Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 7/ISCC.exe'),(Join-Path $root '.tools/InnoSetup/ISCC.exe')) | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
    }
    if (!$Iscc) { throw 'Install Inno Setup 6.7+ and supply -Iscc if needed.' }
    $installers=Join-Path $output 'installer'
    & $Iscc "/DAppVersion=$Version" "/DPublishDir=$desktop" "/DOutputDir=$installers" (Join-Path $root 'Launcher/Installer/Launcher.iss')
    if ($LASTEXITCODE -ne 0) { throw 'Installer packaging failed.' }
    Get-ChildItem -LiteralPath $installers -Filter '*.exe' | Select-Object FullName,Length
}
Write-Host "Self-contained Windows x64 launcher: $desktop"
