param(
    [switch]$AllowHardware,
    [string]$Receiver = '',
    [switch]$Acoustic,
    [string]$MicrophoneDevice = '',
    [ValidateRange(2, 30)][int]$Seconds = 8,
    [ValidateRange(0, 100)][int]$Volume = 30,
    [ValidateSet('Debug', 'Release')][string]$Configuration = 'Release',
    [switch]$NoBuild,
    [string]$OutputDirectory = ''
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'common.ps1')
$run = New-AirBridgeRun 'hardware' $OutputDirectory
Add-AirBridgeProvenance $run
$run.acousticOutputProven = $false
$run.acousticScope = 'direct-pyatv-test-tone'
$run.fullPipelineAcousticallyProven = $false
$run.acousticStatus = 'skipped'
$run.criteria = [ordered]@{ receiver = $Receiver; seconds = $Seconds; volume = $Volume; acousticRequested = [bool]$Acoustic }

function Read-HardwareEvidence($Check) {
    try {
        if (-not (Test-Path -LiteralPath $Check.stdout -PathType Leaf)) { throw 'Diagnostic did not produce an evidence file.' }
        $evidence = [IO.File]::ReadAllText($Check.stdout) | ConvertFrom-Json
        $Check.evidence = $evidence
        if ($Check.timedOut -or $evidence.status -eq 'failed') { throw 'Diagnostic criteria failed; inspect the saved JSON and stderr evidence.' }
        if ($evidence.status -eq 'inconclusive' -and $Check.exitCode -eq 2) {
            $Check.status = 'inconclusive'
            $Check.error = $null
        }
        elseif ($Check.exitCode -ne 0 -or $evidence.status -ne 'passed') { throw 'Diagnostic exit status and acceptance criteria did not both pass.' }
        return $evidence
    }
    catch { $Check.status = 'failed'; $Check.error = $_.Exception.Message; return $null }
}

try {
    if (-not $AllowHardware) {
        Add-AirBridgeSkippedCheck $run 'full-pipeline' 'Pass -AllowHardware and an exact -Receiver to authorize Windows audio and receiver operations.'
        Add-AirBridgeSkippedCheck $run 'acoustic-verification' 'No microphone or receiver operation ran.'
    }
    elseif ([string]::IsNullOrWhiteSpace($Receiver) -or ($Acoustic -and [string]::IsNullOrWhiteSpace($MicrophoneDevice))) {
        $run.checks.Add([ordered]@{ name = 'hardware-configuration'; status = 'failed'; error = 'Specify an exact -Receiver; -Acoustic also requires an explicitly selected -MicrophoneDevice.' }) | Out-Null
    }
    else {
        $diagnosticsProject = Join-Path $script:AirBridgeRoot 'tools\AirBridge.Diagnostics\AirBridge.Diagnostics.csproj'
        $diagnostics = Join-Path $script:AirBridgeRoot "tools\AirBridge.Diagnostics\bin\$Configuration\net9.0-windows10.0.19041.0\AirBridge.Diagnostics.dll"
        $ready = $true
        if (-not $NoBuild) {
            $build = Invoke-AirBridgeCheck $run 'diagnostics-build' 'dotnet' @('build', $diagnosticsProject, '-c', $Configuration, '--nologo') 180
            $ready = $build.status -eq 'passed'
        }
        if ($ready -and -not (Test-Path -LiteralPath $diagnostics -PathType Leaf)) {
            $run.checks.Add([ordered]@{ name = 'diagnostics-artifact'; status = 'failed'; error = 'Diagnostics executable is absent; build it or omit -NoBuild.' }) | Out-Null
            $ready = $false
        }
        if ($ready) {
            # Each subprocess acquires the shared machine-local hardware lease;
            # operations are sequential and never hold two leases at once.
            $pipeline = Invoke-AirBridgeCheck $run 'full-pipeline' 'dotnet' @($diagnostics, '--full-pipeline', $Receiver, [string]$Seconds, [string]$Volume) 200 -Environment @{ AIRBRIDGE_RUN_HARDWARE_TESTS = '1' }
            $pipelineEvidence = Read-HardwareEvidence $pipeline
            if ($pipelineEvidence -and $pipeline.status -eq 'passed' -and
                ($pipelineEvidence.verification.status -ne 'Verified' -or -not $pipelineEvidence.cleanup_completed -or $pipelineEvidence.acoustic_output_proven)) {
                $pipeline.status = 'failed'
                $pipeline.error = 'Full pipeline must prove local PCM criteria and cleanup without claiming acoustic output.'
            }
            if ($Acoustic -and $pipeline.status -eq 'passed') {
                $python = Join-Path $script:AirBridgeRoot '.venv\Scripts\python.exe'
                $microphone = Invoke-AirBridgeCheck $run 'acoustic-verification' $python @(
                    (Join-Path $script:AirBridgeRoot 'src\AirBridge.RaopHost\mic_verify.py'),
                    '--device', $MicrophoneDevice, '--target', $Receiver, '--seconds', [string]$Seconds, '--volume', [string]$Volume
                ) 160 -Environment @{ AIRBRIDGE_RUN_HARDWARE_TESTS = '1' }
                $microphoneEvidence = Read-HardwareEvidence $microphone
                if ($microphoneEvidence -and $microphone.status -eq 'passed' -and
                    ($microphoneEvidence.classification -ne 'clean_tone' -or -not $microphoneEvidence.acoustic_output_proven)) {
                    $microphone.status = 'failed'
                    $microphone.error = 'Acoustic verification must detect a clean tone and exit successfully.'
                }
                $run.acousticStatus = $microphone.status
                $run.acousticOutputProven = $microphone.status -eq 'passed'
            }
            else {
                Add-AirBridgeSkippedCheck $run 'acoustic-verification' 'Use -Acoustic -MicrophoneDevice with -AllowHardware after the full pipeline passes. Local PCM alone does not prove audible output.'
            }
        }
    }
}
catch { $run.checks.Add([ordered]@{ name = 'hardware-wrapper'; status = 'failed'; error = $_.Exception.Message }) | Out-Null }
finally { $completed = Complete-AirBridgeRun $run }

if (-not $completed) { exit 1 }
if (-not $AllowHardware -or -not $run.acousticOutputProven -or @($run.checks | Where-Object { $_.status -eq 'inconclusive' }).Count -gt 0) {
    $run.status = if (-not $AllowHardware) { 'skipped' } else { 'inconclusive' }
    [IO.File]::WriteAllText((Join-Path $run.outputDirectory 'report.json'), ($run | ConvertTo-Json -Depth 20))
    Write-Host 'Acoustic output remains unproven; evidence distinguishes local PCM results from audible-output results.'
    exit 2
}
