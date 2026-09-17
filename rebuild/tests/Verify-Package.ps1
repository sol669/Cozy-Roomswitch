param([Parameter(Mandatory)][string]$PublishPath, [Parameter(Mandatory)][string]$ExpectedVersion, [Parameter(Mandatory)][bool]$SelfContained)
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path -LiteralPath $PublishPath).Path
foreach ($name in @('CozyRoomswitch.exe','CozyRoomswitch.dll','CozyRoomswitch.deps.json','CozyRoomswitch.runtimeconfig.json','App.xbf','CozyRoomswitch.pri','Assets/AppIcon/RoomSwitcher.ico','README-Portable.txt')) {
    $file = Get-Item -LiteralPath (Join-Path $root $name)
    if ($file.Length -eq 0) { throw "Empty required file: $name" }
}
$winUiResources = Get-Item -LiteralPath (Join-Path $root 'CozyRoomswitch.pri')
if ($winUiResources.Length -lt 1MB) { throw 'WinUI resource package is unexpectedly small.' }
$config = Get-Content -Raw -LiteralPath (Join-Path $root 'CozyRoomswitch.runtimeconfig.json') | ConvertFrom-Json
if ($SelfContained) {
    foreach ($name in @('coreclr.dll','hostfxr.dll','Microsoft.UI.Xaml.dll')) {
        if (-not (Test-Path -LiteralPath (Join-Path $root $name))) { throw "Missing offline runtime: $name" }
    }
    if (-not $config.runtimeOptions.includedFrameworks) { throw 'Offline runtime config is framework-dependent' }
} else {
    if (Test-Path -LiteralPath (Join-Path $root 'coreclr.dll')) { throw 'Standard package contains offline runtime' }
    if (-not $config.runtimeOptions.framework -and -not $config.runtimeOptions.frameworks) { throw 'Missing standard runtime dependency' }
}
& (Join-Path $PSScriptRoot 'Verify-AppIcon.ps1') -PublishPath $root -ExpectedVersion $ExpectedVersion
"PASS: complete $ExpectedVersion package; self-contained=$SelfContained; compiled XAML, resources and dependencies verified."
