param([string]$Configuration = 'Release', [switch]$Snapshots, [switch]$BrowserQA, [switch]$NativeUI, [string]$OutputDirectory = '')
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'common.ps1')
$run = New-AirBridgeRun 'verify' $OutputDirectory
Add-AirBridgeProvenance $run
Invoke-AirBridgeCheck $run 'dotnet-version' 'dotnet' @('--version') 15 | Out-Null
Invoke-AirBridgeCheck $run 'node-version' 'node' @('--version') 15 | Out-Null
$python = Join-Path $script:AirBridgeRoot '.venv\Scripts\python.exe'
Invoke-AirBridgeCheck $run 'python-version' $python @('--version') 15 | Out-Null
$testDirectory = Join-Path $run.outputDirectory 'test-results'
Invoke-AirBridgeCheck $run 'dotnet-tests' 'dotnet' @('test', (Join-Path $script:AirBridgeRoot 'tests\AirBridge.Tests\AirBridge.Tests.csproj'), '-c', $Configuration, '--filter', 'Category!=Hardware', '--logger', 'trx;LogFileName=AirBridge.Tests.trx', '--results-directory', $testDirectory, '--blame-hang-timeout', '90s') 300 | Out-Null
Invoke-AirBridgeCheck $run 'deterministic-agent-evals' 'dotnet' @('test', (Join-Path $script:AirBridgeRoot 'tests\AirBridge.Evals\AirBridge.Evals.csproj'), '-c', $Configuration, '--logger', 'trx;LogFileName=AirBridge.Evals.trx', '--results-directory', $testDirectory, '--blame-hang-timeout', '90s') 180 | Out-Null
Invoke-AirBridgeCheck $run 'python-tests' $python @('-m', 'unittest', 'discover', '-s', (Join-Path $script:AirBridgeRoot 'src\AirBridge.RaopHost'), '-p', 'test_*.py', '-v') 120 | Out-Null
Invoke-AirBridgeCheck $run 'release-policy-tests' $python @('-m', 'unittest', 'discover', '-s', (Join-Path $script:AirBridgeRoot 'tests'), '-p', 'test_release.py', '-v') 30 | Out-Null
Invoke-AirBridgeCheck $run 'browser-extension-tests' 'node' @('--test', (Join-Path $script:AirBridgeRoot 'tests\browser-extension.test.js'), (Join-Path $script:AirBridgeRoot 'tests\firefox-extension.test.js')) 120 | Out-Null
Test-AirBridgeHostPing $run | Out-Null
Invoke-AirBridgeCheck $run 'managed-host-ping' 'dotnet' @('run', '--project', (Join-Path $script:AirBridgeRoot 'tools\AirBridge.Diagnostics\AirBridge.Diagnostics.csproj'), '-c', $Configuration, '--', '--ping') 120 | Out-Null
$shell = Join-Path $PSHOME 'powershell.exe'
if (-not (Test-Path -LiteralPath $shell)) { $shell = Join-Path $PSHOME 'pwsh.exe' }
Invoke-AirBridgeCheck $run 'tooling-tests' $shell @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', (Join-Path $PSScriptRoot 'test-tooling.ps1'), '-OutputDirectory', (Join-Path $run.outputDirectory 'tooling')) 30 | Out-Null
Invoke-AirBridgeCheck $run 'fixture-qa' $shell @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', (Join-Path $PSScriptRoot 'fixture-qa.ps1'), '-Configuration', $Configuration, '-OutputDirectory', (Join-Path $run.outputDirectory 'fixture')) 120 | Out-Null
if ($BrowserQA) {
    try { Invoke-AirBridgeCheck $run 'browser-e2e' 'node' (Get-AirBridgeNpmArguments @('run', 'test:e2e')) 180 -Environment @{ AIRBRIDGE_BROWSER_OUTPUT = (Join-Path $run.outputDirectory 'browser') } | Out-Null }
    catch { $run.checks.Add([ordered]@{ name = 'browser-e2e'; status = 'failed'; error = $_.Exception.Message }) | Out-Null }
}
else { Add-AirBridgeSkippedCheck $run 'browser-e2e' 'Run bootstrap.ps1 -BrowserQA, then verify.ps1 -BrowserQA for actual Chromium extension QA.' }
if ($NativeUI) {
    Invoke-AirBridgeCheck $run 'native-ui' $shell @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', (Join-Path $PSScriptRoot 'ui-qa.ps1'), '-Configuration', $Configuration, '-OutputDirectory', (Join-Path $run.outputDirectory 'native-ui'), '-RequireAvailable') 180 | Out-Null
}
else { Add-AirBridgeSkippedCheck $run 'native-ui' 'Run verify.ps1 -NativeUI with WinAppCLI installed for native accessibility/invoke/input assertions.' }
if ($Snapshots) {
    Invoke-AirBridgeCheck $run 'snapshot-matrix' $shell @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', (Join-Path $PSScriptRoot 'snapshots.ps1'), '-Configuration', $Configuration, '-OutputDirectory', (Join-Path $run.outputDirectory 'snapshots')) 180 | Out-Null
}
else { Add-AirBridgeSkippedCheck $run 'snapshot-matrix' 'Run verify.ps1 -Snapshots for the optional visual matrix.' }
Add-AirBridgeSkippedCheck $run 'windows-audio' 'Requires explicit AIRBRIDGE_RUN_HARDWARE_TESTS=1 and a working Windows audio device.'
Add-AirBridgeSkippedCheck $run 'receiver-and-acoustic' 'Requires reserved receivers, selected microphone and explicit hardware opt-in; software checks do not prove audible output.'
Add-AirBridgeSkippedCheck $run 'live-model' 'Paid model evals are separate, explicit runs outside the solution.'
Add-AirBridgeSkippedCheck $run 'install-upgrade-uninstall' 'Requires a disposable Windows VM and packaged artifacts.'
if (-not (Complete-AirBridgeRun $run)) { exit 1 }
