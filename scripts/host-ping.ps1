param([string]$Executable = '', [string]$OutputDirectory = '')
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'common.ps1')
$run = New-AirBridgeRun 'host-ping' $OutputDirectory
Add-AirBridgeProvenance $run
Test-AirBridgeHostPing $run $Executable | Out-Null
if (-not (Complete-AirBridgeRun $run)) { exit 1 }
