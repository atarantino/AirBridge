param([string]$OutputDirectory = '')
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'common.ps1')
$run = New-AirBridgeRun 'doctor' $OutputDirectory
Add-AirBridgeProvenance $run
$sdk = Invoke-AirBridgeCheck $run 'dotnet-version' 'dotnet' @('--version') 15
Invoke-AirBridgeCheck $run 'dotnet-sdks' 'dotnet' @('--list-sdks') 15 | Out-Null
$node = Invoke-AirBridgeCheck $run 'node-version' 'node' @('--version') 15
if ($node.status -eq 'passed') {
    $expected = ([IO.File]::ReadAllText((Join-Path $script:AirBridgeRoot '.node-version'))).Trim()
    if (([IO.File]::ReadAllText($node.stdout)).Trim().TrimStart('v') -ne $expected) { $node.status = 'failed'; $node.error = "Use Node $expected (see .node-version)." }
}
$python = Join-Path $script:AirBridgeRoot '.venv\Scripts\python.exe'
Invoke-AirBridgeCheck $run 'python-version' $python @('-c', 'import sys; print(sys.version); sys.exit(0 if sys.version_info[:2] == (3, 12) else 1)') 15 | Out-Null
Invoke-AirBridgeCheck $run 'python-dependencies' $python @('-m', 'pip', 'check') 30 | Out-Null
Test-AirBridgeHostPing $run | Out-Null
Add-AirBridgeSkippedCheck $run 'hardware' 'Doctor does not capture audio, discover receivers, or test audible output.'
if (-not (Complete-AirBridgeRun $run)) { Write-Error 'Setup is incomplete. Run scripts\bootstrap.ps1, then inspect report.json.'; exit 1 }
