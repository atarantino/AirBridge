param([string]$Configuration = 'Release', [string]$OutputDirectory = '', [switch]$NoBuild)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'common.ps1')
$run = New-AirBridgeRun 'snapshots' $OutputDirectory
Add-AirBridgeProvenance $run
if (-not $NoBuild) {
    $build = Invoke-AirBridgeCheck $run 'build' 'dotnet' @('build', (Join-Path $script:AirBridgeRoot 'src\AirBridge.App\AirBridge.App.csproj'), '-c', $Configuration) 180
    if ($build.status -ne 'passed') { Complete-AirBridgeRun $run | Out-Null; exit 1 }
}
$app = Join-Path $script:AirBridgeRoot ("src\AirBridge.App\bin\$Configuration\net9.0-windows10.0.19041.0\AirBridge.App.exe")
$images = Join-Path $run.outputDirectory 'images'
New-Item -ItemType Directory -Path $images -Force | Out-Null
$environment = @{ AIRBRIDGE_DATA_DIR = (Join-Path $run.outputDirectory 'profile'); AIRBRIDGE_RUN_ID = $run.runId }
foreach ($theme in @('Dark', 'Light')) {
    foreach ($scale in @('1', '1.5', '2')) {
        foreach ($text in @('100', '150')) {
            $name = "flyout-$theme-scale$scale-text$text"
            $environment.AIRBRIDGE_TEXT_SCALE_PERCENT = $text
            $path = Join-Path $images ($name + '.png')
            $check = Invoke-AirBridgeCheck $run $name $app @('--snapshot-flyout', $path, $theme, $scale) 20 -Environment $environment
            if ($check.status -eq 'passed' -and -not (Test-Path -LiteralPath $path)) { $check.status = 'failed'; $check.error = 'Snapshot command did not create its image.' }
        }
    }
    $environment.AIRBRIDGE_TEXT_SCALE_PERCENT = '100'
    foreach ($surface in @('snapshot', 'snapshot-settings', 'snapshot-activity')) {
        $name = "$surface-$theme"
        $path = Join-Path $images ($name + '.png')
        $check = Invoke-AirBridgeCheck $run $name $app @("--$surface", $path, $theme) 20 -Environment $environment
        if ($check.status -eq 'passed' -and -not (Test-Path -LiteralPath $path)) { $check.status = 'failed'; $check.error = 'Snapshot command did not create its image.' }
    }
    foreach ($text in @('100', '150')) {
        $environment.AIRBRIDGE_TEXT_SCALE_PERCENT = $text
        $name = "settings-updates-$theme-text$text"
        $path = Join-Path $images ($name + '.png')
        $check = Invoke-AirBridgeCheck $run $name $app @('--snapshot-settings', $path, $theme, '5') 20 -Environment $environment
        if ($check.status -eq 'passed' -and -not (Test-Path -LiteralPath $path)) { $check.status = 'failed'; $check.error = 'Snapshot command did not create its image.' }
    }
    $environment.AIRBRIDGE_TEXT_SCALE_PERCENT = '100'
    foreach ($state in @('listening', 'thinking', 'silence', 'response', 'pairing', 'confirmation')) {
        $name = "hud-$theme-$state"
        $path = Join-Path $images ($name + '.png')
        $check = Invoke-AirBridgeCheck $run $name $app @('--snapshot-hud', $path, $theme, $state) 20 -Environment $environment
        if ($check.status -eq 'passed' -and -not (Test-Path -LiteralPath $path)) { $check.status = 'failed'; $check.error = 'Snapshot command did not create its image.' }
    }
}
Add-AirBridgeSkippedCheck $run 'visual-review' 'Image generation is not visual acceptance. Inspect the PNG matrix for clipping, overlap, contrast and text-size behavior.'
Add-AirBridgeSkippedCheck $run 'native-ui-interactions' 'Snapshots do not prove focus, tray placement, mouse/keyboard input, global shortcuts or screen-reader behavior.'
if (-not (Complete-AirBridgeRun $run)) { exit 1 }
