# Static layout/style regression checks. No application or WinUI window is started.
$ErrorActionPreference = 'Stop'
$source = Join-Path $PSScriptRoot '../src/RoomSwitcherTray.Core'
$window = Get-Content -LiteralPath (Join-Path $source 'WinUiSettingsWindow.cs') -Raw
$picker = Get-Content -LiteralPath (Join-Path $source 'ScenarioIconPicker.cs') -Raw
[xml]$xaml = Get-Content -LiteralPath (Join-Path $source 'App.xaml') -Raw
$namespaces = [Xml.XmlNamespaceManager]::new($xaml.NameTable)
$namespaces.AddNamespace('p', 'http://schemas.microsoft.com/winfx/2006/xaml/presentation')
$namespaces.AddNamespace('x', 'http://schemas.microsoft.com/winfx/2006/xaml')
$selected = $xaml.SelectSingleNode('//p:Style[@x:Key="RoomSelectedNavigationButtonStyle"]', $namespaces)
if ($null -eq $selected) { throw 'Missing dedicated selected-navigation style.' }
foreach ($stateName in @('Normal','PointerOver','Pressed')) {
    $state = $selected.SelectSingleNode(".//p:VisualState[@x:Name='$stateName']", $namespaces)
    if ($null -eq $state -or $state.ChildNodes.Count -ne 0) { throw "Selection changes in $stateName state." }
}
$presenter = $selected.SelectSingleNode('.//p:ContentPresenter', $namespaces)
foreach ($property in @('Background','Foreground')) {
    if ($presenter.GetAttribute($property) -ne "{TemplateBinding $property}") { throw "Selection must share $property with the icon." }
}
if ($window -notmatch 'Настройки Cozy Roomswitch' -or $window -notmatch 'UpdateWindowTitle\(\)') {
    throw 'The compact native window title is missing.'
}
if ($window -match 'SettingsTitleCell|TitleSpacerCell|TitleBandHeight') {
    throw 'The removed in-page settings title still reserves layout space.'
}
$top = [double][regex]::Match($window, 'ContentTopMargin = (\d+)').Groups[1].Value
# Worst case: name + icon + letters + four monitors + audio + volume mode + value; 3 headers.
# The page is scrollable when both optional rows are visible on a short screen.
$height = $top + 13 * 46 + 13 * 5
if ($height -gt 750 -or $window -notmatch 'return PageScroll\(panel\)') { throw "Scenario overflow is not handled: $height" }
if ($top -le 4) { throw 'Missing requested breathing room.' }
if ($window -notmatch 'Margin = new Thickness\(2, 0, 15, 0\)') { throw 'Device heading columns moved incorrectly.' }
if (Test-Path -LiteralPath (Join-Path $source 'QuietToolTip.cs')) { throw 'Delayed settings tooltip implementation returned.' }
if (($window + $picker) -match 'QuietToolTip|ToolTipService|new ToolTip') { throw 'Settings tooltips returned.' }
if ($picker -notmatch 'DefaultFlyoutPresenterStyle' -or $picker -notmatch 'CornerRadius\(8\)' -or
    $picker -notmatch 'IsDefaultShadowEnabledProperty, true') { throw 'Palette must retain native rounded/shadowed flyout styling.' }
if ($picker.IndexOf('items.Children.Add(letters)') -lt $picker.IndexOf('items.Children.Add(grid)')) { throw 'Letters must be after icons.' }
$flat = $xaml.SelectSingleNode('//p:Style[@x:Key="RoomPaletteButtonStyle"]', $namespaces)
if ($null -eq $flat -or $flat.SelectSingleNode('p:Setter[@Property="Background"]', $namespaces).Value -ne 'Transparent') {
    throw 'Palette icons must not have persistent tiles.'
}
if ($window -match 'StartupVolumeInput|0% — mute' -or
    -not $window.Contains('new[] { T("NoChange"), T("SetVolume") }') -or
    -not $window.Contains('VolumeSlider') -or
    -not $window.Contains('Minimum = 0') -or -not $window.Contains('Maximum = 100') -or
    -not $window.Contains('VolumePercent = null')) {
    throw 'Two-mode slider-based startup-volume UI/default contract is missing.'
}
"PASS: stable selection, compact native title, aligned headers, no settings tooltips, flat rounded palette, Letters last, two-mode volume slider; scrollable full height $height logical pixels."
