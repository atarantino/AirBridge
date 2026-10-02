param([string]$Configuration = 'Release', [switch]$SkipTests, [string]$OutputDirectory = '')
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'common.ps1')
$run = New-AirBridgeRun 'package' $OutputDirectory
Add-AirBridgeProvenance $run
$python = Join-Path $script:AirBridgeRoot '.venv\Scripts\python.exe'
$publish = Join-Path $script:AirBridgeRoot 'artifacts\publish'
$raopPublish = Join-Path $publish 'RaopHost'
$installer = Join-Path $script:AirBridgeRoot 'artifacts\AirBridge-Setup.exe'
$toolManifest = Join-Path $script:AirBridgeRoot '.config\dotnet-tools.json'

function Invoke-PackageCheck([string]$Name, [string]$FilePath, [string[]]$Arguments, [int]$TimeoutSeconds = 240) {
    $check = Invoke-AirBridgeCheck $run $Name $FilePath $Arguments $TimeoutSeconds
    if ($check.status -ne 'passed') { throw "$Name failed. Inspect $($run.outputDirectory)." }
}

try {
    $wixVersion = (ConvertFrom-Json -InputObject ([IO.File]::ReadAllText($toolManifest))).tools.wix.version
    $wixExtension = 'WixToolset.BootstrapperApplications.wixext/' + $wixVersion
    $shell = Join-Path $PSHOME 'powershell.exe'
    if (-not (Test-Path -LiteralPath $shell)) { $shell = Join-Path $PSHOME 'pwsh.exe' }
    Invoke-PackageCheck 'bootstrap' $shell @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', (Join-Path $PSScriptRoot 'bootstrap.ps1'), '-OutputDirectory', (Join-Path $run.outputDirectory 'bootstrap')) 360
    if (-not $SkipTests) {
        Invoke-PackageCheck 'verify' $shell @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', (Join-Path $PSScriptRoot 'verify.ps1'), '-Configuration', $Configuration, '-OutputDirectory', (Join-Path $run.outputDirectory 'verify')) 900
    }
    else { Add-AirBridgeSkippedCheck $run 'verify' 'Explicit -SkipTests bypass; this package has not passed the release gate.' }
    Invoke-PackageCheck 'packaging-dependencies' $python @('-m', 'pip', 'install', '--disable-pip-version-check', 'pyinstaller==6.16.0')
    if (Test-Path -LiteralPath $publish) {
        $resolvedPublish = (Resolve-Path -LiteralPath $publish).ProviderPath
        if ($resolvedPublish -ne [IO.Path]::GetFullPath((Join-Path $script:AirBridgeRoot 'artifacts\publish'))) { throw "Refusing to clean unexpected directory: $resolvedPublish" }
        Remove-Item -LiteralPath $resolvedPublish -Recurse -Force
    }
    Invoke-PackageCheck 'dotnet-publish' 'dotnet' @('publish', (Join-Path $script:AirBridgeRoot 'src\AirBridge.App\AirBridge.App.csproj'), '-c', $Configuration, '-r', 'win-x64', '--self-contained', 'true', '-p:PublishSingleFile=true', '-o', $publish)
    New-Item -ItemType Directory -Force -Path $raopPublish | Out-Null
    Invoke-PackageCheck 'raop-publish' $python @('-m', 'PyInstaller', '--noconfirm', '--clean', '--onefile', '--name', 'AirBridge.RaopHost', '--collect-all', 'pyatv', '--collect-all', 'miniaudio', '--distpath', $raopPublish, '--workpath', (Join-Path $script:AirBridgeRoot 'artifacts\pyinstaller-work'), '--specpath', (Join-Path $script:AirBridgeRoot 'artifacts'), (Join-Path $script:AirBridgeRoot 'src\AirBridge.RaopHost\host.py')) 360
    $ping = Test-AirBridgeHostPing $run (Join-Path $raopPublish 'AirBridge.RaopHost.exe') 'packaged-host-ping'
    if ($ping.status -ne 'passed') { throw 'Packaged RAOP host failed its protocol handshake.' }
    Invoke-PackageCheck 'wix-restore' 'dotnet' @('tool', 'restore', '--tool-manifest', $toolManifest)
    Invoke-PackageCheck 'wix-extension' 'dotnet' @('wix', 'extension', 'add', $wixExtension)
    $sourceDefine = 'SourceRoot=' + $script:AirBridgeRoot
    Invoke-PackageCheck 'msi-build' 'dotnet' @('wix', 'build', (Join-Path $script:AirBridgeRoot 'installer\wix\Package.wxs'), '-d', $sourceDefine, '-arch', 'x64', '-out', (Join-Path $script:AirBridgeRoot 'artifacts\AirBridge.msi'))
    Invoke-PackageCheck 'installer-build' 'dotnet' @('wix', 'build', (Join-Path $script:AirBridgeRoot 'installer\wix\Bundle.wxs'), '-d', $sourceDefine, '-arch', 'x64', '-ext', $wixExtension, '-out', $installer)
    $run.artifacts = @((Join-Path $script:AirBridgeRoot 'artifacts\AirBridge.msi'), $installer, $publish)
    $run.artifactHashes = @()
    foreach ($artifact in @((Join-Path $script:AirBridgeRoot 'artifacts\AirBridge.msi'), $installer, (Join-Path $raopPublish 'AirBridge.RaopHost.exe'))) {
        $run.artifactHashes += [ordered]@{ path = $artifact; bytes = (Get-Item -LiteralPath $artifact).Length; sha256 = (Get-FileHash -LiteralPath $artifact -Algorithm SHA256).Hash.ToLowerInvariant() }
    }
    Add-AirBridgeSkippedCheck $run 'install-upgrade-uninstall' 'Build and host ping do not prove installation; run on a disposable Windows VM.'
    Write-Host "Installer: $installer"
}
catch { $run.checks.Add([ordered]@{ name = 'package'; status = 'failed'; error = $_.Exception.Message }) | Out-Null }
if (-not (Complete-AirBridgeRun $run)) { exit 1 }
