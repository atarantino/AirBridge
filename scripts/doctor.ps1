param([string]$OutputDirectory = '')
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'common.ps1')
$run = New-AirBridgeRun 'doctor' $OutputDirectory
Add-AirBridgeProvenance $run
$sdk = Invoke-AirBridgeCheck $run 'dotnet-version' 'dotnet' @('--version') 15
Invoke-AirBridgeCheck $run 'dotnet-sdks' 'dotnet' @('--list-sdks') 15 | Out-Null
Test-AirBridgeNodeVersion $run | Out-Null
$python = Join-Path $script:AirBridgeRoot '.venv\Scripts\python.exe'
Test-AirBridgePythonVersion $run $python | Out-Null
Invoke-AirBridgeCheck $run 'python-dependencies' $python @('-m', 'pip', 'check') 30 | Out-Null
Test-AirBridgeHostPing $run | Out-Null
Add-AirBridgeSkippedCheck $run 'hardware' 'Doctor does not capture audio, discover receivers, or test audible output.'
if (-not (Complete-AirBridgeRun $run)) { Write-Error 'Setup is incomplete. Run scripts\bootstrap.ps1, then inspect report.json.'; exit 1 }
