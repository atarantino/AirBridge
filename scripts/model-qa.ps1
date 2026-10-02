param(
    [switch]$AllowPaid,
    [ValidatePattern('^[A-Za-z][A-Za-z0-9_]*$')][string]$KeyEnvironmentVariable = 'AIRBRIDGE_MODEL_EVAL_KEY',
    [ValidateSet('Debug', 'Release')][string]$Configuration = 'Release',
    [ValidateRange(30, 1200)][int]$TimeoutSeconds = 900,
    [string]$OutputDirectory = ''
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'common.ps1')
$run = New-AirBridgeRun 'model' $OutputDirectory
Add-AirBridgeProvenance $run
$run.paidApiAuthorized = [bool]$AllowPaid
$run.responseMeaningGraded = $false
$apiKey = $null
$environment = @{}
try {
    if (-not $AllowPaid) {
        Add-AirBridgeSkippedCheck $run 'live-model-evals' 'Pass -AllowPaid to explicitly authorize billed model requests; default verification does not run them.'
    }
    else {
        $apiKey = [Environment]::GetEnvironmentVariable($KeyEnvironmentVariable)
        if ([string]::IsNullOrWhiteSpace($apiKey)) {
            $run.checks.Add([ordered]@{ name = 'model-configuration'; status = 'failed'; error = "Set $KeyEnvironmentVariable for this explicit run; key values are never arguments or report fields." }) | Out-Null
        }
        else {
            $environment = @{ OPENAI_API_KEY = $apiKey; AIRBRIDGE_MODEL_EVALS = '1' }
            Invoke-AirBridgeCheck $run 'live-model-evals' 'dotnet' @(
                'test', (Join-Path $script:AirBridgeRoot 'tests\AirBridge.ModelEvals\AirBridge.ModelEvals.csproj'),
                '-c', $Configuration, '--logger', 'trx;LogFileName=AirBridge.ModelEvals.trx',
                '--results-directory', (Join-Path $run.outputDirectory 'test-results')
            ) $TimeoutSeconds -Environment $environment | Out-Null
        }
    }
}
catch { $run.checks.Add([ordered]@{ name = 'model-wrapper'; status = 'failed'; error = $_.Exception.Message }) | Out-Null }
finally {
    # Credentials exist only in the owned child's environment. No parent API or
    # hardware environment variable is changed by this wrapper.
    $environment.Clear()
    $apiKey = $null
    $completed = Complete-AirBridgeRun $run
}
if (-not $completed) { exit 1 }
if (-not $AllowPaid) {
    $run.status = 'skipped'
    [IO.File]::WriteAllText((Join-Path $run.outputDirectory 'report.json'), ($run | ConvertTo-Json -Depth 20))
    exit 2
}
