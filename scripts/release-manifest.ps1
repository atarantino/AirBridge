param(
    [Parameter(Mandatory = $true)][string]$Plan,
    [string]$PackageReport = 'artifacts/qa/ci-package/report.json',
    [string]$OutputDirectory = 'artifacts/release-candidate'
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'common.ps1')
$run = New-AirBridgeRun 'release-inspection' ''
Add-AirBridgeProvenance $run
try {
    $OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
    New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
    foreach ($name in @('AirBridge.msi', 'AirBridge-Setup.exe')) {
        Copy-Item -LiteralPath (Join-Path $script:AirBridgeRoot "artifacts\$name") -Destination (Join-Path $OutputDirectory $name)
    }
    $xml = Join-Path $run.outputDirectory 'package.wxs'
    $extracted = Join-Path $run.outputDirectory 'extracted'
    $check = Invoke-AirBridgeCheck $run 'inspect-msi' 'dotnet' @('wix', 'msi', 'decompile', (Join-Path $OutputDirectory 'AirBridge.msi'), '-x', $extracted, '-o', $xml, '-intermediateFolder', (Join-Path $run.outputDirectory 'temp')) 120
    if ($check.status -ne 'passed') { throw 'MSI inspection failed.' }
    $python = Join-Path $script:AirBridgeRoot '.venv\Scripts\python.exe'
    $check = Invoke-AirBridgeCheck $run 'release-manifest' $python @((Join-Path $PSScriptRoot 'release.py'), 'manifest', '--directory', $OutputDirectory, '--package-report', $PackageReport, '--xml', $xml, '--extracted', $extracted, '--plan', $Plan) 30
    if ($check.status -ne 'passed') { throw 'Release manifest failed validation.' }
}
catch { $run.checks.Add([ordered]@{ name = 'release-manifest'; status = 'failed'; error = $_.Exception.Message }) | Out-Null }
if (-not (Complete-AirBridgeRun $run)) { exit 1 }
