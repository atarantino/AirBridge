# Regression checks for quoting, native exit handling and process deadlines.
param([string]$OutputDirectory = '')
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'common.ps1')
$run = New-AirBridgeRun 'tooling-tests' $OutputDirectory
$python = Join-Path $script:AirBridgeRoot '.venv\Scripts\python.exe'
$values = @('space in path', 'quote"inside', 'C:\space path\', '', 'plain', 'backslash\"quote', ('AirBridge ' + [char]0x203a))
$quoted = Invoke-AirBridgeCheck $run 'argument-roundtrip' $python (@('-c', 'import json,sys; print(json.dumps(sys.argv[1:]))') + $values) 15
if ($quoted.status -eq 'passed') {
    $actual = ConvertFrom-Json -InputObject ([IO.File]::ReadAllText($quoted.stdout))
    $matches = $actual.Count -eq $values.Count
    for ($index = 0; $matches -and $index -lt $values.Count; $index++) { $matches = [string]::Equals($actual[$index], $values[$index], [StringComparison]::Ordinal) }
    if (-not $matches) { $quoted.status = 'failed'; $quoted.error = 'Native argument escaping changed an argument.' }
}
$unicode = Invoke-AirBridgeCheck $run 'utf8-output' $python @('-c', 'print(chr(8250) + " " + chr(20013))') 15
if ($unicode.status -eq 'passed' -and ([IO.File]::ReadAllText($unicode.stdout)).Trim() -ne ([string][char]0x203a + ' ' + [char]0x4e2d)) { $unicode.status = 'failed'; $unicode.error = 'Native UTF-8 output was decoded using a console code page.' }
$exit = Invoke-AirBridgeCheck $run 'native-exit-detection' $python @('-c', 'import sys; print("stdout"); print("stderr",file=sys.stderr); sys.exit(7)') 15 -Quiet
if ($exit.status -eq 'failed' -and $exit.exitCode -eq 7 -and ([IO.File]::ReadAllText($exit.stdout)).Trim() -eq 'stdout' -and ([IO.File]::ReadAllText($exit.stderr)).Trim() -eq 'stderr') { $exit.status = 'passed'; $exit.error = $null; $exit.expectedExitCode = 7 }
else { $exit.status = 'failed'; $exit.error = 'Failed to observe native failure and separate logs.' }
$timeout = Invoke-AirBridgeCheck $run 'deadline-cleanup' $python @('-c', 'import time; time.sleep(10)') 1 -Quiet
if ($timeout.timedOut -and $timeout.status -eq 'failed' -and $timeout.durationMs -lt 8000) { $timeout.status = 'passed'; $timeout.error = $null; $timeout.expectedTimeout = $true }
else { $timeout.status = 'failed'; $timeout.error = 'Deadline did not terminate the owned process within the cleanup budget.' }
Write-Host "native-exit-detection: $($exit.status); deadline-cleanup: $($timeout.status)"
$keys = @('OPENAI_API_KEY', 'AIRBRIDGE_RUN_HARDWARE_TESTS', 'AIRBRIDGE_MODEL_EVALS')
$previous = @{}
try {
    foreach ($key in $keys) { $previous[$key] = [Environment]::GetEnvironmentVariable($key, 'Process'); [Environment]::SetEnvironmentVariable($key, 'test-opt-in-must-not-inherit', 'Process') }
    Invoke-AirBridgeCheck $run 'offline-defaults' $python @('-c', 'import os; assert not os.getenv("OPENAI_API_KEY"); assert not os.getenv("AIRBRIDGE_RUN_HARDWARE_TESTS"); assert not os.getenv("AIRBRIDGE_MODEL_EVALS"); assert os.getenv("AIRBRIDGE_DATA_DIR"); print("isolated")') 15 | Out-Null
}
finally { foreach ($key in $keys) { [Environment]::SetEnvironmentVariable($key, $previous[$key], 'Process') } }
Invoke-AirBridgeCheck $run 'explicit-opt-in' $python @('-c', 'import os; assert os.getenv("AIRBRIDGE_MODEL_EVALS") == "1"; print("explicit override works")') 15 -Environment @{ AIRBRIDGE_MODEL_EVALS = '1' } | Out-Null
if (-not (Complete-AirBridgeRun $run)) { exit 1 }
