param(
    [string]$Configuration = 'Debug',
    [string]$Fixture = 'healthy',
    [switch]$Live,
    [switch]$NoBuild,
    [string]$DataDirectory = '',
    [string]$OutputDirectory = '',
    [switch]$Wait,
    [int]$DurationSeconds = 120
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'common.ps1')
if ($DurationSeconds -lt 1 -or $DurationSeconds -gt 3600) { throw 'DurationSeconds must be between 1 and 3600.' }
$run = New-AirBridgeRun 'session' $OutputDirectory
Add-AirBridgeProvenance $run
if (-not $DataDirectory) { $DataDirectory = if ($Live) { Get-AirBridgeProfile } else { Join-Path $run.outputDirectory 'profile' } }
$run.profile = [IO.Path]::GetFullPath($DataDirectory)
New-Item -ItemType Directory -Path $run.profile -Force | Out-Null
if (-not $NoBuild) {
    $build = Invoke-AirBridgeCheck $run 'build' 'dotnet' @('build', (Join-Path $script:AirBridgeRoot 'src\AirBridge.App\AirBridge.App.csproj'), '-c', $Configuration) 180
    if ($build.status -ne 'passed') { Complete-AirBridgeRun $run | Out-Null; exit 1 }
}
$executable = Join-Path $script:AirBridgeRoot ("src\AirBridge.App\bin\$Configuration\net9.0-windows10.0.19041.0\AirBridge.App.exe")
if (-not (Test-Path -LiteralPath $executable)) { throw "App executable missing: $executable. Run without -NoBuild." }
$pipe = 'AirBridge.Debug.' + [Guid]::NewGuid().ToString('N')
$sessionFile = Join-Path $run.outputDirectory 'app-session.json'
$arguments = @('--debug-pipe', $pipe, '--session-file', $sessionFile)
if (-not $Live) { $arguments += @('--fixture', $Fixture) }
$keys = @('AIRBRIDGE_DATA_DIR', 'AIRBRIDGE_RUN_ID', 'AIRBRIDGE_BUILD_COMMIT', 'OPENAI_API_KEY', 'AIRBRIDGE_RUN_HARDWARE_TESTS', 'AIRBRIDGE_MODEL_EVALS')
$previous = @{}
$process = $null
try {
    foreach ($key in $keys) { $previous[$key] = [Environment]::GetEnvironmentVariable($key, 'Process') }
    $env:AIRBRIDGE_DATA_DIR = $run.profile
    $env:AIRBRIDGE_RUN_ID = $run.runId
    $env:AIRBRIDGE_BUILD_COMMIT = $run.commit
    Remove-Item Env:OPENAI_API_KEY -ErrorAction SilentlyContinue
    Remove-Item Env:AIRBRIDGE_RUN_HARDWARE_TESTS -ErrorAction SilentlyContinue
    Remove-Item Env:AIRBRIDGE_MODEL_EVALS -ErrorAction SilentlyContinue
    $process = Start-Process -FilePath $executable -ArgumentList (($arguments | ForEach-Object { ConvertTo-AirBridgeArgument $_ }) -join ' ') -WorkingDirectory $script:AirBridgeRoot -WindowStyle Hidden -RedirectStandardOutput (Join-Path $run.outputDirectory 'app.stdout.log') -RedirectStandardError (Join-Path $run.outputDirectory 'app.stderr.log') -PassThru
}
finally { foreach ($key in $keys) { [Environment]::SetEnvironmentVariable($key, $previous[$key], 'Process') } }
$manifest = [ordered]@{ schemaVersion = 1; runId = $run.runId; executable = $executable; profile = $run.profile; pid = $process.Id; processStartedUtc = $process.StartTime.ToUniversalTime().ToString('o'); pipe = $pipe; mode = $(if ($Live) { 'production' } else { 'fixture' }); scenario = $(if ($Live) { $null } else { $Fixture }); ready = $false; appSessionFile = $sessionFile; cleanupCommand = "& '$(Join-Path $PSScriptRoot 'debug.ps1')' -PipeName '$pipe' -Method shutdown" }
$manifestPath = Join-Path $run.outputDirectory 'session.json'
$watch = [Diagnostics.Stopwatch]::StartNew()
$ready = $false
try {
    while ($watch.Elapsed.TotalSeconds -lt 30) {
        if ($process.HasExited) { throw "App exited before readiness (code $($process.ExitCode))." }
        if (Test-Path -LiteralPath $sessionFile) {
            try { $appSession = [IO.File]::ReadAllText($sessionFile) | ConvertFrom-Json }
            catch { Start-Sleep -Milliseconds 100; continue }
            if ($appSession.pid -ne $process.Id) { throw 'Session manifest PID does not identify the owned process.' }
            if ($appSession.ready -eq $true) { $ready = $true; break }
            if ($appSession.state -eq 'failed') { throw 'App reported failed initialization. Inspect its session manifest and profile logs.' }
        }
        Start-Sleep -Milliseconds 100
    }
    if (-not $ready) { throw 'App did not report readiness within 30 seconds.' }
    $manifest.ready = $true
    [IO.File]::WriteAllText($manifestPath, ($manifest | ConvertTo-Json -Depth 10))
    $run.checks.Add([ordered]@{ name = 'app-readiness'; status = 'passed'; pid = $process.Id; durationMs = $watch.ElapsedMilliseconds }) | Out-Null
    Write-Host "Ready: $executable"
    Write-Host "Profile: $($run.profile)"
    Write-Host "PID: $($process.Id); debug pipe: $pipe"
    Write-Host "Session: $manifestPath"
    Write-Host "Cleanup: $($manifest.cleanupCommand)"
    if ($Wait) {
        if (-not $process.WaitForExit($DurationSeconds * 1000)) {
            & (Join-Path $PSScriptRoot 'debug.ps1') -PipeName $pipe -Method shutdown | Out-Null
            if (-not $process.WaitForExit(10000)) { Stop-AirBridgeOwnedProcess $process; throw 'App did not shut down gracefully after the session deadline.' }
        }
        if ($process.ExitCode -ne 0) { throw "App exited with $($process.ExitCode)." }
        $manifest.stopped = $true
    }
}
catch {
    $manifest.error = $_.Exception.Message
    Stop-AirBridgeOwnedProcess $process
    $run.checks.Add([ordered]@{ name = 'app-session'; status = 'failed'; error = $_.Exception.Message }) | Out-Null
}
finally {
    [IO.File]::WriteAllText($manifestPath, ($manifest | ConvertTo-Json -Depth 10))
    $process.Dispose()
}
if (-not (Complete-AirBridgeRun $run)) { exit 1 }
