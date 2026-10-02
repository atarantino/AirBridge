param(
    [ValidateSet('Debug', 'Release')][string] $Configuration = 'Release',
    [ValidateSet('healthy', 'no-receivers', 'partial-failure', 'pairing', 'reconnect')]
    [string[]] $Scenario = @('healthy', 'no-receivers', 'partial-failure', 'pairing', 'reconnect'),
    [string] $OutputDirectory,
    [switch] $NoBuild
)

$ErrorActionPreference = 'Stop'
$repository = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $repository ('artifacts\verification\fixture-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
}
$OutputDirectory = [System.IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
if (-not $NoBuild) {
    & dotnet build (Join-Path $repository 'src\AirBridge.App\AirBridge.App.csproj') -c $Configuration --nologo
    if ($LASTEXITCODE -ne 0) { throw 'App build failed.' }
}
$executable = Join-Path $repository "src\AirBridge.App\bin\$Configuration\net9.0-windows10.0.19041.0\AirBridge.App.exe"
if (-not (Test-Path -LiteralPath $executable -PathType Leaf)) { throw "App executable not found: $executable" }

function Invoke-Debug {
    param([string] $Method, [hashtable] $Values = @{}, [switch] $AllowError)
    $json = ConvertTo-Json -InputObject $Values -Depth 10 -Compress
    & (Join-Path $PSScriptRoot 'debug.ps1') -PipeName $script:currentPipe -Method $Method -Parameters $json -AllowError:$AllowError
}

function Assert-Condition {
    param([bool] $Condition, [string] $Message)
    if (-not $Condition) { throw $Message }
}

$results = [System.Collections.Generic.List[object]]::new()
$failed = $false
foreach ($case in $Scenario) {
    $sessionDirectory = Join-Path $OutputDirectory ($case + '-' + [Guid]::NewGuid().ToString('N').Substring(0, 8))
    New-Item -ItemType Directory -Path $sessionDirectory -Force | Out-Null
    $profile = Join-Path $sessionDirectory 'profile'
    $manifestPath = Join-Path $sessionDirectory 'session.json'
    $script:currentPipe = 'AirBridge.Qa.' + [Guid]::NewGuid().ToString('N')
    $process = $null
    $timer = [System.Diagnostics.Stopwatch]::StartNew()
    $passed = $false
    $errorMessage = $null
    try {
        $arguments = '--fixture ' + $case + ' --data-dir "' + $profile + '" --debug-pipe ' + $script:currentPipe + ' --session-file "' + $manifestPath + '"'
        $process = Start-Process -FilePath $executable -ArgumentList $arguments -PassThru -WindowStyle Hidden
        $deadline = [DateTime]::UtcNow.AddSeconds(20)
        $state = $null
        do {
            if ($process.HasExited) { throw "Fixture exited before ready with code $($process.ExitCode)." }
            try { $state = & (Join-Path $PSScriptRoot 'debug.ps1') -PipeName $script:currentPipe -Method state -TimeoutMs 1000 }
            catch { Start-Sleep -Milliseconds 100 }
            if ($state.result.ready) { break }
        } while ([DateTime]::UtcNow -lt $deadline)
        Assert-Condition ($null -ne $state -and $state.result.ready) 'Fixture did not become ready within 20 seconds.'
        Assert-Condition ($state.result.mode -eq 'fixture') 'QA accidentally launched a non-fixture session.'
        Assert-Condition ($state.result.scenario -eq $case) 'Fixture scenario did not match.'
        $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
        Assert-Condition ($manifest.pid -eq $process.Id) 'Session manifest PID did not match the owned process.'
        $state | ConvertTo-Json -Depth 30 | Set-Content -LiteralPath (Join-Path $sessionDirectory 'initial-state.json') -Encoding UTF8

        # Validate the launched UI bridge, including JSON type errors, before any
        # legitimate mutations. Protocol rejection must preserve readiness and Idle.
        $invalidRequests = @(
            @{ method = 'start'; values = @{ receiverIds = 17 } },
            @{ method = 'events'; values = @{ afterSequence = 'invalid' } },
            @{ method = 'wait-for-state'; values = @{ state = '99999'; timeoutMs = 1 } }
        )
        if ($case -ne 'no-receivers') {
            $invalidRequests += @{ method = 'volume'; values = @{ receiverId = 'fixture-a'; percent = '47' } }
        }
        $protocolEvidence = [System.Collections.Generic.List[object]]::new()
        foreach ($invalid in $invalidRequests) {
            $rejected = Invoke-Debug $invalid.method $invalid.values -AllowError
            Assert-Condition (-not $rejected.ok -and $rejected.error.code -eq 'invalid_params') "Malformed $($invalid.method) parameters must return invalid_params."
            $protocolEvidence.Add($rejected)
        }
        $unchanged = Invoke-Debug state
        Assert-Condition ($unchanged.result.ready -and $unchanged.result.route.state -eq 'Idle') 'Malformed parameters changed readiness or the idle route.'

        if ($case -eq 'no-receivers') {
            Assert-Condition (@($state.result.receivers).Count -eq 0) 'No-receivers fixture discovered a receiver.'
            $rejected = Invoke-Debug start @{ receiverIds = @('fixture-a') } -AllowError
            Assert-Condition (-not $rejected.ok) 'Starting an absent fixture receiver must fail.'
        }
        else {
            if ($case -eq 'pairing') {
                $pairing = @($state.result.receivers | Where-Object { $_.id -eq 'fixture-b' })[0]
                Assert-Condition $pairing.requiresPairing 'Pairing fixture must initially require pairing.'
                $missingCode = Invoke-Debug pair @{ receiverId = 'fixture-b' } -AllowError
                Assert-Condition (-not $missingCode.ok -and $missingCode.error.code -eq 'invalid_params') 'A missing pairing code must return invalid_params before pairing begins.'
                $protocolEvidence.Add($missingCode)
                $afterMissingCode = Invoke-Debug state
                $stillUnpaired = @($afterMissingCode.result.receivers | Where-Object { $_.id -eq 'fixture-b' })[0]
                Assert-Condition ($stillUnpaired.requiresPairing -and $afterMissingCode.result.route.state -eq 'Idle') 'Malformed pairing parameters changed pairing or playback state.'
                $badCode = Invoke-Debug pair @{ receiverId = 'fixture-b'; code = '0000' } -AllowError
                Assert-Condition (-not $badCode.ok) 'An incorrect fixture pairing code was accepted.'
                Invoke-Debug pair @{ receiverId = 'fixture-b'; code = '1234' } | Out-Null
            }
            Invoke-Debug start @{ receiverIds = @('fixture-a', 'fixture-b') } | Out-Null
            $expectedState = if ($case -eq 'partial-failure') { 'Degraded' } else { 'Streaming' }
            Invoke-Debug wait-for-state @{ state = $expectedState; timeoutMs = 10000 } | Out-Null
            # The real group gate waits up to 10 seconds for an unavailable receiver,
            # then prefills the healthy leg. Observe that fallback instead of accepting silence.
            $pcmDeadline = [DateTime]::UtcNow.AddSeconds(15)
            do {
                $state = Invoke-Debug state
                $stats = @($state.result.fixture.receivers)
                $healthyCount = if ($case -eq 'partial-failure') { 1 } else { 2 }
                $carriedPcm = $stats.Count -eq $healthyCount -and @($stats | Where-Object { $_.bytesReceived -le 0 -or $_.signalBlocks -le 0 }).Count -eq 0
                if ($carriedPcm) { break }
                Start-Sleep -Milliseconds 100
            } while ([DateTime]::UtcNow -lt $pcmDeadline)
            Assert-Condition $carriedPcm 'Generated signal did not traverse the real controller and receiver PCM pipes.'
            Assert-Condition ($state.result.fixture.capture.blocksGenerated -gt 0) 'The fixture capture source did not generate audio blocks.'
            if ($case -eq 'partial-failure') {
                $failedReceiver = @($state.result.playback | Where-Object { $_.id -eq 'fixture-b' })[0]
                Assert-Condition ($failedReceiver.state -eq 'Failed') 'Injected failed receiver did not report Failed.'
            }
            if ($case -eq 'reconnect') {
                $retryReceiver = @($stats | Where-Object { $_.id -eq 'fixture-b' })[0]
                Assert-Condition (@($retryReceiver.transitions) -contains 'Reconnecting') 'Reconnect fixture did not exercise a retry transition.'
            }
            Invoke-Debug volume @{ receiverId = 'fixture-a'; percent = 47 } | Out-Null
            $state = Invoke-Debug state
            $volume = @($state.result.playback | Where-Object { $_.id -eq 'fixture-a' })[0].volume
            Assert-Condition ($volume -eq 47) 'The controller did not apply fixture volume.'
            $state | ConvertTo-Json -Depth 30 | Set-Content -LiteralPath (Join-Path $sessionDirectory 'streaming-state.json') -Encoding UTF8
            Invoke-Debug snapshot @{ surface = 'flyout'; path = (Join-Path $profile 'qa-flyout-streaming.png') } | Out-Null
            Invoke-Debug stop | Out-Null
            Invoke-Debug wait-for-state @{ state = 'Idle'; timeoutMs = 5000 } | Out-Null
            $stopped = Invoke-Debug state
            Assert-Condition (@($stopped.result.playback).Count -eq 0) 'Stopping left active receiver playback.'
            Assert-Condition (-not $stopped.result.fixture.capture.running) 'Stopping left the fixture capture task running.'
        }
        $snapshotPath = Join-Path $profile 'qa-flyout.png'
        $protocolEvidence | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath (Join-Path $sessionDirectory 'protocol-rejections.json') -Encoding UTF8
        Invoke-Debug snapshot @{ surface = 'flyout'; path = $snapshotPath } | Out-Null
        Assert-Condition (Test-Path -LiteralPath $snapshotPath -PathType Leaf) 'The real fixture flyout snapshot was not saved.'
        $events = Invoke-Debug events
        $events | ConvertTo-Json -Depth 30 | Set-Content -LiteralPath (Join-Path $sessionDirectory 'events.json') -Encoding UTF8
        Invoke-Debug shutdown | Out-Null
        if (-not $process.WaitForExit(15000)) { throw 'Fixture did not exit within the bounded shutdown window.' }
        Assert-Condition ($process.ExitCode -eq 0) "Fixture exited with code $($process.ExitCode)."
        $finalManifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
        Assert-Condition (-not $finalManifest.ready) 'Shutdown left session readiness enabled.'
        $passed = $true
        Write-Host "PASS fixture $case (owned PID $($process.Id))"
    }
    catch {
        $failed = $true
        $errorMessage = $_.Exception.Message
        Write-Warning "FAIL fixture ${case}: $errorMessage"
        if ($null -ne $process -and -not $process.HasExited) {
            try {
                Invoke-Debug state | ConvertTo-Json -Depth 30 | Set-Content -LiteralPath (Join-Path $sessionDirectory 'failure-state.json') -Encoding UTF8
                Invoke-Debug events | ConvertTo-Json -Depth 30 | Set-Content -LiteralPath (Join-Path $sessionDirectory 'failure-events.json') -Encoding UTF8
            } catch { }
        }
    }
    finally {
        if ($null -ne $process -and -not $process.HasExited) {
            try { Invoke-Debug shutdown | Out-Null } catch { }
            if (-not $process.WaitForExit(5000)) {
                # This Process object identifies only the instance launched above; never stop other app instances.
                $process.Kill()
                $process.WaitForExit(3000) | Out-Null
            }
        }
        $timer.Stop()
        $results.Add([pscustomobject]@{ scenario = $case; passed = $passed; error = $errorMessage; durationMs = $timer.ElapsedMilliseconds; evidence = $sessionDirectory })
        if ($null -ne $process) { $process.Dispose() }
    }
}
$results | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath (Join-Path $OutputDirectory 'results.json') -Encoding UTF8
if ($failed) { throw "One or more fixture QA scenarios failed. Evidence: $OutputDirectory" }
Write-Host "Fixture QA passed. Evidence: $OutputDirectory"
