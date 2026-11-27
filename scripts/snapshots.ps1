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
function Save-Snapshot([string]$Name, [string]$Surface, [string[]]$Options) {
    $path = Join-Path $images ($Name + '.png')
    $check = Invoke-AirBridgeCheck $run $Name $app (@($Surface, $path) + $Options) 20 -Environment $environment
    if ($check.status -eq 'passed' -and -not (Test-Path -LiteralPath $path)) { $check.status = 'failed'; $check.error = 'Snapshot command did not create its image.' }
}
foreach ($theme in @('Dark', 'Light')) {
    foreach ($scale in @('1', '1.5', '2')) {
        foreach ($text in @('100', '150')) {
            $environment.AIRBRIDGE_TEXT_SCALE_PERCENT = $text
            Save-Snapshot "flyout-$theme-scale$scale-text$text" '--snapshot-flyout' @($theme, $scale)
        }
    }
    $environment.AIRBRIDGE_TEXT_SCALE_PERCENT = '100'
    foreach ($surface in @('snapshot', 'snapshot-settings', 'snapshot-activity')) {
        Save-Snapshot "$surface-$theme" "--$surface" @($theme)
    }
    foreach ($text in @('100', '150')) {
        $environment.AIRBRIDGE_TEXT_SCALE_PERCENT = $text
        Save-Snapshot "settings-updates-$theme-text$text" '--snapshot-settings' @($theme, '5')
    }
    $environment.AIRBRIDGE_TEXT_SCALE_PERCENT = '100'
    foreach ($state in @('listening', 'thinking', 'silence', 'response', 'pairing', 'confirmation')) {
        Save-Snapshot "hud-$theme-$state" '--snapshot-hud' @($theme, $state)
    }
}
Add-AirBridgeSkippedCheck $run 'visual-review' 'Image generation is not visual acceptance. Inspect the PNG matrix for clipping, overlap, contrast and text-size behavior.'
Add-AirBridgeSkippedCheck $run 'native-ui-interactions' 'Snapshots do not prove focus, tray placement, mouse/keyboard input, global shortcuts or screen-reader behavior.'
if (-not (Complete-AirBridgeRun $run)) { exit 1 }
