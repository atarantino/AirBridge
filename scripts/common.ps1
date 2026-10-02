# Shared PowerShell 5.1-compatible development helpers. Dot-source this file.
Set-StrictMode -Version 2.0
$script:AirBridgeRoot = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))

function Get-AirBridgeHash([string]$Value) {
    $sha = [Security.Cryptography.SHA256]::Create()
    try { return ([BitConverter]::ToString($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($Value)))).Replace('-', '').ToLowerInvariant() }
    finally { $sha.Dispose() }
}

function Get-AirBridgeProfile {
    return Join-Path $script:AirBridgeRoot ('artifacts\profiles\' + (Get-AirBridgeHash $script:AirBridgeRoot.ToLowerInvariant()).Substring(0, 12))
}

function New-AirBridgeRun([string]$Kind, [string]$OutputDirectory = '') {
    $id = [DateTime]::UtcNow.ToString('yyyyMMddTHHmmssfffZ') + '-' + [Guid]::NewGuid().ToString('N').Substring(0, 8)
    if (-not $OutputDirectory) { $OutputDirectory = Join-Path $script:AirBridgeRoot ('artifacts\qa\' + $Kind + '-' + $id) }
    $directory = [IO.Path]::GetFullPath($OutputDirectory)
    New-Item -ItemType Directory -Path $directory -Force | Out-Null
    return [ordered]@{ schemaVersion = 1; kind = $Kind; runId = $id; startedUtc = [DateTime]::UtcNow.ToString('o'); repository = $script:AirBridgeRoot; outputDirectory = $directory; profile = (Join-Path $directory 'profile'); commit = $null; dirty = $null; diffSha256 = $null; checks = New-Object Collections.ArrayList }
}

function ConvertTo-AirBridgeArgument([string]$Value) {
    # CommandLineToArgvW escaping, including trailing backslashes before a quote.
    if ($Value -ne '' -and $Value -notmatch '[\s"]') { return $Value }
    $escaped = [regex]::Replace($Value, '(\\*)"', '$1$1\"')
    $escaped = [regex]::Replace($escaped, '(\\+)$', '$1$1')
    return '"' + $escaped + '"'
}

function New-AirBridgeProcess([string]$FilePath, [string[]]$Arguments = @(), [string]$WorkingDirectory = $script:AirBridgeRoot, [hashtable]$Environment = @{}) {
    $command = Get-Command $FilePath -ErrorAction Stop
    $info = New-Object Diagnostics.ProcessStartInfo
    $info.FileName = $command.Source
    $info.Arguments = (($Arguments | ForEach-Object { ConvertTo-AirBridgeArgument $_ }) -join ' ')
    $info.WorkingDirectory = $WorkingDirectory
    $info.UseShellExecute = $false
    $info.CreateNoWindow = $true
    $info.RedirectStandardInput = $true
    $info.RedirectStandardOutput = $true
    $info.RedirectStandardError = $true
    # Do not inherit the parent console code page: nested PowerShell can default
    # to OEM 437, corrupting UTF-8 Git diffs and their provenance hashes.
    $info.StandardOutputEncoding = [Text.UTF8Encoding]::new($false)
    $info.StandardErrorEncoding = [Text.UTF8Encoding]::new($false)
    # The default verification tiers must never inherit paid API/hardware opt-ins.
    $info.EnvironmentVariables.Remove('OPENAI_API_KEY')
    $info.EnvironmentVariables.Remove('AIRBRIDGE_RUN_HARDWARE_TESTS')
    $info.EnvironmentVariables.Remove('AIRBRIDGE_MODEL_EVALS')
    $info.EnvironmentVariables['AIRBRIDGE_DATA_DIR'] = Get-AirBridgeProfile
    $info.EnvironmentVariables['PYTHONUTF8'] = '1'
    foreach ($key in $Environment.Keys) { $info.EnvironmentVariables[$key] = [string]$Environment[$key] }
    $process = New-Object Diagnostics.Process
    $process.StartInfo = $info
    if (-not $process.Start()) { throw "Could not start $FilePath" }
    return $process
}

function Stop-AirBridgeOwnedProcess($Process) {
    if ($Process.HasExited) { return }
    # PID came from our Process.Start, never from a process-name search.
    $killer = New-Object Diagnostics.ProcessStartInfo
    $killer.FileName = Join-Path $env:SystemRoot 'System32\taskkill.exe'
    $killer.Arguments = '/PID ' + $Process.Id + ' /T /F'
    $killer.UseShellExecute = $false
    $killer.CreateNoWindow = $true
    $killer.RedirectStandardOutput = $true
    $killer.RedirectStandardError = $true
    $killProcess = [Diagnostics.Process]::Start($killer)
    try { $killProcess.StandardOutput.ReadToEnd() | Out-Null; $killProcess.StandardError.ReadToEnd() | Out-Null; $killProcess.WaitForExit(5000) | Out-Null }
    finally { $killProcess.Dispose() }
    if (-not $Process.WaitForExit(5000)) { throw "Owned process $($Process.Id) did not terminate." }
}

function Invoke-AirBridgeCheck($Run, [string]$Name, [string]$FilePath, [string[]]$Arguments = @(), [int]$TimeoutSeconds = 180, [string]$WorkingDirectory = $script:AirBridgeRoot, [hashtable]$Environment = @{}, [string]$StandardInput = '', [switch]$Quiet) {
    $stdoutPath = Join-Path $Run.outputDirectory ($Name + '.stdout.log')
    $stderrPath = Join-Path $Run.outputDirectory ($Name + '.stderr.log')
    $watch = [Diagnostics.Stopwatch]::StartNew()
    $check = [ordered]@{ name = $Name; status = 'failed'; executable = $FilePath; arguments = $Arguments; exitCode = $null; timedOut = $false; durationMs = 0; stdout = $stdoutPath; stderr = $stderrPath; error = $null }
    $process = $null
    try {
        $childEnvironment = @{ AIRBRIDGE_DATA_DIR = $Run.profile; AIRBRIDGE_RUN_ID = $Run.runId; AIRBRIDGE_BUILD_COMMIT = $Run.commit }
        foreach ($key in $Environment.Keys) { $childEnvironment[$key] = $Environment[$key] }
        $process = New-AirBridgeProcess -FilePath $FilePath -Arguments $Arguments -WorkingDirectory $WorkingDirectory -Environment $childEnvironment
        $check.executable = $process.StartInfo.FileName
        $outTask = $process.StandardOutput.ReadToEndAsync()
        $errTask = $process.StandardError.ReadToEndAsync()
        if ($StandardInput) { $process.StandardInput.WriteLine($StandardInput) }
        $process.StandardInput.Close()
        if (-not $process.WaitForExit($TimeoutSeconds * 1000)) {
            $check.timedOut = $true
            $check.error = "Exceeded deadline of $TimeoutSeconds seconds."
            Stop-AirBridgeOwnedProcess $process
        }
        $check.exitCode = $process.ExitCode
        [IO.File]::WriteAllText($stdoutPath, $outTask.GetAwaiter().GetResult())
        [IO.File]::WriteAllText($stderrPath, $errTask.GetAwaiter().GetResult())
        if (-not $check.timedOut -and $check.exitCode -eq 0) { $check.status = 'passed' }
        elseif (-not $check.error) { $check.error = "Native command exited with $($check.exitCode)." }
    }
    catch { $check.error = $_.Exception.Message; if ($process -and -not $process.HasExited) { Stop-AirBridgeOwnedProcess $process } }
    finally { if ($process) { $process.Dispose() }; $watch.Stop(); $check.durationMs = $watch.ElapsedMilliseconds }
    $Run.checks.Add($check) | Out-Null
    if (-not $Quiet) { Write-Host ("{0}: {1} ({2} ms)" -f $Name, $check.status, $check.durationMs) }
    return $check
}

function Add-AirBridgeSkippedCheck($Run, [string]$Name, [string]$Reason) {
    $Run.checks.Add([ordered]@{ name = $Name; status = 'skipped'; reason = $Reason }) | Out-Null
}

function Add-AirBridgeProvenance($Run) {
    $gitHead = Invoke-AirBridgeCheck $Run 'git-head' 'git' @('rev-parse', 'HEAD') 10
    if ($gitHead.status -eq 'passed') { $Run.commit = ([IO.File]::ReadAllText($gitHead.stdout)).Trim() }
    $gitStatus = Invoke-AirBridgeCheck $Run 'git-status' 'git' @('status', '--porcelain=v1') 10
    $gitDiff = Invoke-AirBridgeCheck $Run 'git-diff' 'git' @('diff', '--binary', 'HEAD') 10
    $untracked = Invoke-AirBridgeCheck $Run 'git-untracked' 'git' @('ls-files', '--others', '--exclude-standard', '-z') 10
    if ($gitStatus.status -eq 'passed') { $Run.dirty = -not [string]::IsNullOrWhiteSpace([IO.File]::ReadAllText($gitStatus.stdout)) }
    $Run.untrackedFiles = @()
    if ($untracked.status -eq 'passed') {
        foreach ($relative in ([IO.File]::ReadAllText($untracked.stdout)).Split([char]0)) {
            if (-not $relative) { continue }
            $file = [IO.Path]::GetFullPath((Join-Path $script:AirBridgeRoot $relative))
            if (-not $file.StartsWith($script:AirBridgeRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Git returned an untracked path outside the checkout.' }
            if (Test-Path -LiteralPath $file -PathType Leaf) {
                $Run.untrackedFiles += [ordered]@{ path = $relative; sha256 = (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash.ToLowerInvariant() }
            }
        }
    }
    if ($gitDiff.status -eq 'passed') {
        $diff = [IO.File]::ReadAllText($gitDiff.stdout)
        $Run.trackedDiffSha256 = Get-AirBridgeHash $diff
        $Run.diffSha256 = Get-AirBridgeHash ($diff + "`n" + ($Run.untrackedFiles | ConvertTo-Json -Depth 5 -Compress))
    }
    $Run.os = [Environment]::OSVersion.VersionString
    $Run.powerShell = $PSVersionTable.PSVersion.ToString()
}

function Complete-AirBridgeRun($Run) {
    $Run.completedUtc = [DateTime]::UtcNow.ToString('o')
    $failed = @($Run.checks | Where-Object { $_.status -eq 'failed' })
    $Run.status = if ($failed.Count -gt 0) { 'failed' } else { 'passed' }
    $path = Join-Path $Run.outputDirectory 'report.json'
    [IO.File]::WriteAllText($path, ($Run | ConvertTo-Json -Depth 20))
    Write-Host "Report: $path"
    return $failed.Count -eq 0
}

function Test-AirBridgeHostPing($Run, [string]$Executable = '', [string]$Name = 'python-host-ping') {
    $arguments = @()
    if (-not $Executable) { $Executable = Join-Path $script:AirBridgeRoot '.venv\Scripts\python.exe'; $arguments = @('-u', (Join-Path $script:AirBridgeRoot 'src\AirBridge.RaopHost\host.py')) }
    $check = Invoke-AirBridgeCheck $Run $Name $Executable $arguments 30 -StandardInput '{"request_id":"agent-ping","command":"ping"}'
    if ($check.status -eq 'passed') {
        try {
            $lines = @([IO.File]::ReadAllLines($check.stdout) | Where-Object { $_.Trim() })
            if ($lines.Count -ne 1) { throw "Expected exactly one host response, got $($lines.Count)." }
            $response = $lines[0] | ConvertFrom-Json
            if ($response.request_id -ne 'agent-ping' -or $response.ok -ne $true -or $response.result.ok -ne $true) { throw 'Host ping response did not match the protocol contract.' }
            $check.response = $response
        }
        catch { $check.status = 'failed'; $check.error = $_.Exception.Message }
    }
    return $check
}

function Get-AirBridgeNpmArguments([string[]]$Arguments) {
    # Run npm's JavaScript entrypoint directly, avoiding cmd.exe shell quoting.
    $npm = Get-Command npm.cmd -ErrorAction Stop
    $cli = Join-Path (Split-Path -Parent $npm.Source) 'node_modules\npm\bin\npm-cli.js'
    if (-not (Test-Path -LiteralPath $cli)) { throw "Cannot locate npm-cli.js next to $($npm.Source). Use the standard Node installation selected by .node-version." }
    return @($cli) + $Arguments
}
