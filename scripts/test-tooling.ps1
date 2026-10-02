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
$previousInputEncoding = [Console]::InputEncoding
$previousPythonIoEncoding = [Environment]::GetEnvironmentVariable('PYTHONIOENCODING', 'Process')
try {
    # Force the hosted Windows PowerShell startup encoding that exposed the
    # BOM; the test's parent encoding is restored in finally.
    [Console]::InputEncoding = [Text.UTF8Encoding]::new($true)
    [Environment]::SetEnvironmentVariable('PYTHONIOENCODING', 'cp1252', 'Process')
    $forcedInputEncoding = [Console]::InputEncoding
    $payload = ConvertTo-Json -InputObject @{ message = ('AirBridge ' + [char]0x203a + ' ' + [char]0x4e2d) } -Compress
    Invoke-AirBridgeCheck $run 'utf8-stdin-json-eof' $python @('-c', 'import json,sys; assert sys.stdin.encoding.replace("-", "").lower() == "utf8", "inherited Python IO encoding leaked"; data=sys.stdin.buffer.read(); assert not data.startswith(bytes([239,187,191])), "stdin contains a UTF-8 BOM"; assert data[:1] == b"{", "JSON is not the first byte"; assert data.endswith(b"\n") and data.count(b"\n") == 1, "expected one JSON line"; value=json.loads(data.decode("utf-8")); assert value["message"] == "AirBridge " + chr(8250) + " " + chr(20013), "Unicode JSON changed"; print("BOM-free Unicode JSON and EOF")') 15 -StandardInput $payload | Out-Null
    Invoke-AirBridgeCheck $run 'empty-stdin-eof' $python @('-c', 'import sys; assert sys.stdin.buffer.read() == b"", "empty stdin contained bytes"; print("empty EOF")') 15 | Out-Null
    $encodingRestored = [Console]::InputEncoding.Equals($forcedInputEncoding) -and [BitConverter]::ToString([Console]::InputEncoding.GetPreamble()) -eq [BitConverter]::ToString($forcedInputEncoding.GetPreamble())
    $run.checks.Add([ordered]@{ name = 'stdin-encoding-restored'; status = $(if ($encodingRestored) { 'passed' } else { 'failed' }); error = $(if ($encodingRestored) { $null } else { 'Native startup changed the caller console input encoding.' }) }) | Out-Null
    $invalidExecutable = Join-Path $run.outputDirectory 'invalid-process.exe'
    [IO.File]::WriteAllText($invalidExecutable, 'Invalid executable for startup-failure regression.')
    $invalid = Invoke-AirBridgeCheck $run 'startup-failure-encoding-restored' $invalidExecutable @() 15 -Quiet
    $encodingRestored = [Console]::InputEncoding.Equals($forcedInputEncoding) -and [BitConverter]::ToString([Console]::InputEncoding.GetPreamble()) -eq [BitConverter]::ToString($forcedInputEncoding.GetPreamble())
    if ($invalid.status -eq 'failed' -and $null -eq $invalid.exitCode -and $encodingRestored) { $invalid.status = 'passed'; $invalid.expectedStartupFailure = $true; $invalid.startupError = $invalid.error; $invalid.error = $null }
    else { $invalid.status = 'failed'; $invalid.error = 'Failed startup did not preserve the caller console input encoding.' }
    Write-Host "stdin-encoding-restored: $($run.checks[$run.checks.Count - 2].status); startup-failure-encoding-restored: $($invalid.status)"
}
finally {
    [Console]::InputEncoding = $previousInputEncoding
    [Environment]::SetEnvironmentVariable('PYTHONIOENCODING', $previousPythonIoEncoding, 'Process')
}
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
