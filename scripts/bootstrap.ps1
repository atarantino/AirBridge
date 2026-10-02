param([switch]$Offline, [switch]$BrowserQA, [string]$OutputDirectory = '')
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'common.ps1')
$run = New-AirBridgeRun 'bootstrap' $OutputDirectory
Add-AirBridgeProvenance $run
Invoke-AirBridgeCheck $run 'dotnet-version' 'dotnet' @('--version') 15 | Out-Null
$node = Invoke-AirBridgeCheck $run 'node-version' 'node' @('--version') 15
if ($node.status -eq 'passed') {
    $expected = ([IO.File]::ReadAllText((Join-Path $script:AirBridgeRoot '.node-version'))).Trim()
    if (([IO.File]::ReadAllText($node.stdout)).Trim().TrimStart('v') -ne $expected) { $node.status = 'failed'; $node.error = "Use Node $expected (see .node-version)." }
}
$python = Join-Path $script:AirBridgeRoot '.venv\Scripts\python.exe'
if (-not (Test-Path -LiteralPath $python)) {
    Invoke-AirBridgeCheck $run 'create-venv' 'py' @('-3.12', '-m', 'venv', (Join-Path $script:AirBridgeRoot '.venv')) 120 | Out-Null
}
$version = Invoke-AirBridgeCheck $run 'python-version' $python @('-c', 'import sys; print(sys.version); sys.exit(0 if sys.version_info[:2] == (3, 12) else 1)') 15
$pipArgs = @('-m', 'pip', 'install', '--disable-pip-version-check', '--requirement', (Join-Path $script:AirBridgeRoot 'src\AirBridge.RaopHost\requirements.lock.txt'))
if ($Offline) { $pipArgs += '--no-index' }
if ($version.status -eq 'passed') { Invoke-AirBridgeCheck $run 'python-restore' $python $pipArgs 240 | Out-Null }
else { Add-AirBridgeSkippedCheck $run 'python-restore' 'A valid tree-local Python 3.12 interpreter is required.' }
$restore = @('restore', (Join-Path $script:AirBridgeRoot 'AirBridge.sln'))
if ($Offline) {
    $offlineSource = Join-Path $script:AirBridgeRoot 'artifacts\nuget-offline-source'
    New-Item -ItemType Directory -Path $offlineSource -Force | Out-Null
    $restore += @('--source', $offlineSource)
}
Invoke-AirBridgeCheck $run 'dotnet-restore' 'dotnet' $restore 240 | Out-Null
if (Test-Path -LiteralPath (Join-Path $script:AirBridgeRoot 'package-lock.json')) {
    try {
        $npmArgs = @('ci', '--no-audit', '--no-fund')
        if ($Offline) { $npmArgs += '--offline' }
        Invoke-AirBridgeCheck $run 'node-restore' 'node' (Get-AirBridgeNpmArguments $npmArgs) 240 | Out-Null
    }
    catch { $run.checks.Add([ordered]@{ name = 'node-restore'; status = 'failed'; error = $_.Exception.Message }) | Out-Null }
}
if ($BrowserQA) {
    Invoke-AirBridgeCheck $run 'browser-restore' 'node' @((Join-Path $script:AirBridgeRoot 'node_modules\playwright\cli.js'), 'install', 'chromium') 240 | Out-Null
}
else { Add-AirBridgeSkippedCheck $run 'browser-restore' 'Use bootstrap.ps1 -BrowserQA to install the optional Chromium QA runtime.' }
Test-AirBridgeHostPing $run | Out-Null
if (-not (Complete-AirBridgeRun $run)) { Write-Error 'Bootstrap failed; inspect report.json and command logs. Install missing SDK/Node/Python tools manually.'; exit 1 }
