param(
    [ValidateSet('Debug', 'Release')][string] $Configuration = 'Release',
    [string] $OutputDirectory = '',
    [switch] $NoBuild,
    [switch] $RequireAvailable
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'common.ps1')
$run = New-AirBridgeRun 'native-ui' $OutputDirectory
$cli = Get-Command winapp -CommandType Application -ErrorAction SilentlyContinue
$run.available = $null -ne $cli
$run.documentation = 'https://learn.microsoft.com/en-us/windows/apps/dev-tools/winapp-cli/ui-automation'
if ($null -eq $cli) {
    $reason = 'Installed WinAppCLI was not found on PATH; native UI actions were not exercised.'
    if ($RequireAvailable) {
        $run.checks.Add([ordered]@{ name = 'native-ui'; status = 'failed'; error = $reason }) | Out-Null
        Complete-AirBridgeRun $run | Out-Null
        exit 1
    }
    Add-AirBridgeSkippedCheck $run 'native-ui' $reason
    $run.status = 'skipped'
    $run.completedUtc = [DateTime]::UtcNow.ToString('o')
    [IO.File]::WriteAllText((Join-Path $run.outputDirectory 'report.json'), ($run | ConvertTo-Json -Depth 20))
    Write-Host "SKIPPED native UI: $reason"
    exit 0
}

Add-AirBridgeProvenance $run
$workflow = [Guid]::NewGuid().ToString('N')
$process = $null
$stdoutTask = $null
$stderrTask = $null
$pipeName = 'AirBridge.UiQa.' + [Guid]::NewGuid().ToString('N')
$sessionPath = Join-Path $run.outputDirectory 'app-session.json'

function Invoke-UiCheck {
    param([string] $Name, [string[]] $Arguments)
    $check = Invoke-AirBridgeCheck $run $Name $cli.Source (@('ui') + $Arguments + @('--json')) 25 -Environment @{ WINAPP_UI_WORKFLOW_ID = $workflow }
    if ($check.status -ne 'passed') { throw "Native UI check '$Name' failed. Inspect $($check.stderr)." }
    $json = [IO.File]::ReadAllText($check.stdout)
    if ([string]::IsNullOrWhiteSpace($json)) { return $null }
    return ConvertFrom-Json -InputObject $json
}

function Find-UiElements {
    param($Elements)
    foreach ($element in $Elements) {
        $element
        if ($null -ne $element.PSObject.Properties['children']) { Find-UiElements $element.children }
    }
}

function Select-UiWindow {
    param($Tree, [string] $Title)
    $matches = @($Tree.windows | Where-Object { $_.title -eq $Title })
    if ($matches.Count -ne 1) { throw "Expected one owned window '$Title'; found $($matches.Count). Inspect recorded UI trees." }
    return $matches[0]
}

function Select-UiAction {
    param($Window, [string] $AutomationId, [string] $AccessibleName)
    $elements = @(Find-UiElements $Window.elements)
    $matches = @($elements | Where-Object { $_.selector -eq $AutomationId })
    if ($matches.Count -eq 0) {
        $matches = @($elements | Where-Object { $_.name -eq $AccessibleName -and $_.isInvokable -and -not $_.isOffscreen })
    }
    if ($matches.Count -ne 1) { throw "Expected one action '$AccessibleName'; found $($matches.Count). Inspect recorded UI trees." }
    return $matches[0].selector
}

try {
    if (-not $NoBuild) {
        $build = Invoke-AirBridgeCheck $run 'build' 'dotnet' @('build', (Join-Path $script:AirBridgeRoot 'src\AirBridge.App\AirBridge.App.csproj'), '-c', $Configuration) 180
        if ($build.status -ne 'passed') { throw 'App build failed.' }
    }
    $executable = Join-Path $script:AirBridgeRoot "src\AirBridge.App\bin\$Configuration\net9.0-windows10.0.19041.0\AirBridge.App.exe"
    if (-not (Test-Path -LiteralPath $executable -PathType Leaf)) { throw 'The requested app build does not exist.' }
    $process = New-AirBridgeProcess -FilePath $executable -Arguments @('--fixture', 'healthy', '--data-dir', $run.profile, '--debug-pipe', $pipeName, '--session-file', $sessionPath) -Environment @{ AIRBRIDGE_BUILD_COMMIT = $run.commit; AIRBRIDGE_RUN_ID = $run.runId }
    $stdoutTask = $process.StandardOutput.ReadToEndAsync()
    $stderrTask = $process.StandardError.ReadToEndAsync()
    $process.StandardInput.Close()
    $run.pid = $process.Id
    $run.pipe = $pipeName
    $run.sessionFile = $sessionPath
    $readyDeadline = [DateTime]::UtcNow.AddSeconds(20)
    $ready = $false
    do {
        if ($process.HasExited) { throw "Fixture app exited before readiness (code $($process.ExitCode))." }
        if (Test-Path -LiteralPath $sessionPath) {
            try {
                $manifest = [IO.File]::ReadAllText($sessionPath) | ConvertFrom-Json
                if ($manifest.pid -ne $process.Id) { throw 'Session manifest did not identify the owned fixture PID.' }
                $ready = $manifest.ready
                if ($manifest.state -eq 'failed') { throw 'Fixture initialization failed.' }
            }
            catch [System.ArgumentException] { }
        }
        if (-not $ready) { Start-Sleep -Milliseconds 100 }
    } while (-not $ready -and [DateTime]::UtcNow -lt $readyDeadline)
    if (-not $ready) { throw 'Fixture app did not become ready.' }

    # Resolve only windows in the PID launched above. Selectors are obtained from
    # the current UIA tree and are refreshed after every UI action.
    $targetPid = $process.Id.ToString()
    Invoke-UiCheck 'wait-flyout' @('wait-for', 'AirBridge quick controls', '-a', $targetPid, '--timeout', '10000') | Out-Null
    $tree = Invoke-UiCheck 'inspect-initial' @('inspect', '-a', $targetPid, '--depth', '12')
    $flyout = Select-UiWindow $tree 'AirBridge quick controls'
    Invoke-UiCheck 'screenshot-initial' @('screenshot', '-w', $flyout.hwnd.ToString(), '--output', (Join-Path $run.outputDirectory 'initial.png')) | Out-Null
    $refresh = Select-UiAction $flyout 'RefreshSpeakers' 'Refresh speakers'
    Invoke-UiCheck 'invoke-refresh' @('invoke', $refresh, '-w', $flyout.hwnd.ToString()) | Out-Null
    Invoke-UiCheck 'wait-refresh-enabled' @('wait-for', 'RefreshSpeakers', '-w', $flyout.hwnd.ToString(), '--property', 'IsEnabled', '--value', 'True', '--timeout', '5000') | Out-Null
    $tree = Invoke-UiCheck 'inspect-refreshed' @('inspect', '-a', $targetPid, '--depth', '12')
    $flyout = Select-UiWindow $tree 'AirBridge quick controls'
    $openSettings = Select-UiAction $flyout 'OpenSettings' 'Open settings'
    Invoke-UiCheck 'invoke-settings' @('invoke', $openSettings, '-w', $flyout.hwnd.ToString()) | Out-Null
    Invoke-UiCheck 'wait-settings' @('wait-for', 'AirBridge settings', '-a', $targetPid, '--timeout', '5000') | Out-Null
    $tree = Invoke-UiCheck 'inspect-settings' @('inspect', '-a', $targetPid, '--depth', '12')
    $settings = Select-UiWindow $tree 'AirBridge Settings'
    Invoke-UiCheck 'screenshot-settings' @('screenshot', '-w', $settings.hwnd.ToString(), '--output', (Join-Path $run.outputDirectory 'settings.png')) | Out-Null
    $close = Select-UiAction $settings 'CloseSettings' 'Close'
    Invoke-UiCheck 'invoke-settings-close' @('invoke', $close, '-w', $settings.hwnd.ToString()) | Out-Null
    Invoke-UiCheck 'wait-settings-gone' @('wait-for', 'AirBridge settings', '-a', $targetPid, '--gone', '--timeout', '5000') | Out-Null
    $final = & (Join-Path $PSScriptRoot 'debug.ps1') -PipeName $pipeName -Method state
    if (-not $final.result.ready -or $final.result.mode -ne 'fixture' -or $final.result.route.state -ne 'Idle') {
        throw 'Native UI navigation changed the fixture route or session readiness unexpectedly.'
    }
    [IO.File]::WriteAllText((Join-Path $run.outputDirectory 'final-state.json'), ($final | ConvertTo-Json -Depth 30))
    & (Join-Path $PSScriptRoot 'debug.ps1') -PipeName $pipeName -Method shutdown | Out-Null
    if (-not $process.WaitForExit(15000)) { throw 'The owned fixture did not exit after UI QA.' }
    if ($process.ExitCode -ne 0) { throw "The owned fixture exited with code $($process.ExitCode)." }
    $run.checks.Add([ordered]@{ name = 'owned-fixture-cleanup'; status = 'passed'; pid = $process.Id }) | Out-Null
}
catch {
    $run.checks.Add([ordered]@{ name = 'native-ui-flow'; status = 'failed'; error = $_.Exception.Message }) | Out-Null
    Write-Warning $_.Exception.Message
}
finally {
    if ($null -ne $process) {
        if (-not $process.HasExited) {
            try { & (Join-Path $PSScriptRoot 'debug.ps1') -PipeName $pipeName -Method shutdown | Out-Null } catch { }
            if (-not $process.WaitForExit(5000)) { Stop-AirBridgeOwnedProcess $process }
        }
        if ($null -ne $stdoutTask) { [IO.File]::WriteAllText((Join-Path $run.outputDirectory 'app.stdout.log'), $stdoutTask.GetAwaiter().GetResult()) }
        if ($null -ne $stderrTask) { [IO.File]::WriteAllText((Join-Path $run.outputDirectory 'app.stderr.log'), $stderrTask.GetAwaiter().GetResult()) }
        $process.Dispose()
    }
    # Cooperative UI workflow reservation is released even when assertions fail.
    Invoke-AirBridgeCheck $run 'release-ui-workflow' $cli.Source @('ui', 'yield', '--json') 10 -Environment @{ WINAPP_UI_WORKFLOW_ID = $workflow } | Out-Null
}
if (-not (Complete-AirBridgeRun $run)) { exit 1 }
