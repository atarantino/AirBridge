param(
    [Parameter(Mandatory = $true)][string]$ArtifactDirectory,
    [Parameter(Mandatory = $true)][string]$PriorMsi,
    [Parameter(Mandatory = $true)][string]$OutputDirectory
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'common.ps1')

# Installation is intentionally restricted to a disposable hosted runner.
if ($env:GITHUB_ACTIONS -ne 'true' -or $env:RUNNER_ENVIRONMENT -ne 'github-hosted') {
    throw 'Installer QA requires a disposable GitHub-hosted Windows runner.'
}
$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
if (-not ([Security.Principal.WindowsPrincipal]::new($identity)).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Installer QA requires an administrative runner session.'
}
$ArtifactDirectory = [IO.Path]::GetFullPath($ArtifactDirectory)
$PriorMsi = [IO.Path]::GetFullPath($PriorMsi)
$run = New-AirBridgeRun 'installer-lifecycle' $OutputDirectory
Add-AirBridgeProvenance $run
$run.runnerImage = $env:ImageOS + '/' + $env:ImageVersion
$run.windows = Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion' |
    Select-Object ProductName, EditionID, CurrentBuild, UBR
$run.environmentLimitations = @('Hosted Windows Server image; Windows 10/11-specific desktop behavior requires a client-OS pass.', 'The runner has developer runtimes installed; absence of system Python/.NET is not established.')
$msi = Join-Path $ArtifactDirectory 'AirBridge.msi'
$bundle = Join-Path $ArtifactDirectory 'AirBridge-Setup.exe'
$provenance = Get-Content -LiteralPath (Join-Path $ArtifactDirectory 'release-validation.json') -Raw | ConvertFrom-Json
$run.artifactSourceCommit = $provenance.sourceCommit
$run.artifactHashes = $provenance.assets
$run.priorMsiSha256 = (Get-FileHash -LiteralPath $PriorMsi -Algorithm SHA256).Hash.ToLowerInvariant()
$installedRoot = Join-Path $env:ProgramFiles 'AirBridge'
$installedApp = Join-Path $installedRoot 'AirBridge.App.exe'
$installedHost = Join-Path $installedRoot 'RaopHost\AirBridge.RaopHost.exe'
$shortcutPath = Join-Path $env:ProgramData 'Microsoft\Windows\Start Menu\Programs\AirBridge\AirBridge.lnk'
$settingsPath = Join-Path $env:LOCALAPPDATA 'AirBridge\settings.json'

function Assert-QA([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw $Message }
}

function Get-AirBridgeProducts([switch]$IncludeBundle) {
    foreach ($registry in @('HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall', 'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall')) {
        Get-ChildItem -LiteralPath $registry | ForEach-Object {
            $product = Get-ItemProperty -LiteralPath $_.PSPath
            $isMsi = $product.PSObject.Properties['WindowsInstaller'] -and $product.WindowsInstaller -eq 1
            if ($product.PSObject.Properties['DisplayName'] -and $product.DisplayName -eq 'AirBridge for Windows' -and ($IncludeBundle -or $isMsi)) {
                [pscustomobject]@{ productCode = $_.PSChildName; version = $product.DisplayVersion }
            }
        }
    }
}

function Invoke-Msi([string]$Name, [string]$Operation, [string]$Package, [switch]$ExpectDowngrade) {
    $log = Join-Path $run.outputDirectory ($Name + '.msi.log')
    $check = Invoke-AirBridgeCheck $run $Name 'msiexec.exe' @($Operation, $Package, '/qn', '/norestart', '/L*v', $log) 180
    if ($ExpectDowngrade) {
        $content = [IO.File]::ReadAllText($log)
        Assert-QA ($check.exitCode -eq 1603 -and $content.Contains('A newer AirBridge version is already installed.')) 'Prior MSI did not reject the downgrade with the expected message.'
        $check.status = 'passed'
        $check.error = $null
        $check.expectedExitCode = 1603
    }
    else {
        if (-not $check.timedOut -and $check.exitCode -eq 3010) { $check.status = 'passed'; $check.error = $null }
        Assert-QA ($check.status -eq 'passed') "$Name failed; inspect $log."
    }
}

function Assert-InstalledPayload([string]$CheckName) {
    $products = @(Get-AirBridgeProducts)
    Assert-QA ($products.Count -eq 1 -and $products[0].version -eq $provenance.version) 'Expected one registered MSI product at the release version.'
    Assert-QA (Test-Path -LiteralPath $shortcutPath) 'Start menu shortcut is missing.'
    foreach ($file in $provenance.packageInspection.msiPayloadFiles) {
        $path = Join-Path $installedRoot $file.file
        Assert-QA ((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant() -eq $file.sha256) ('Installed payload mismatch: ' + $file.file)
    }
    $run.checks.Add([ordered]@{ name = $CheckName; status = 'passed'; product = $products[0]; matchedPayloadFiles = $provenance.packageInspection.msiPayloadFiles.Count }) | Out-Null
}

function Assert-Uninstalled([string]$Name) {
    Assert-QA (@(Get-AirBridgeProducts -IncludeBundle).Count -eq 0) 'Uninstall left an AirBridge product registered.'
    Assert-QA (-not (Test-Path -LiteralPath $installedApp)) 'Uninstall left the installed app executable.'
    Assert-QA (-not (Test-Path -LiteralPath $installedHost)) 'Uninstall left the installed RAOP executable.'
    Assert-QA (-not (Test-Path -LiteralPath $shortcutPath)) 'Uninstall left the Start menu shortcut.'
    $run.checks.Add([ordered]@{ name = $Name; status = 'passed' }) | Out-Null
}

function Test-InstalledApp([string]$Name, [switch]$Fixture) {
    $directory = Join-Path $run.outputDirectory $Name
    New-Item -ItemType Directory -Path $directory -Force | Out-Null
    $pipe = 'AirBridge.InstallerQa.' + [Guid]::NewGuid().ToString('N')
    $manifestPath = Join-Path $directory 'app-session.json'
    $arguments = @('--debug-pipe', $pipe, '--session-file', $manifestPath)
    if ($Fixture) { $arguments += @('--fixture', 'healthy') }
    $process = $null
    $shell = New-Object -ComObject WScript.Shell
    $shortcut = $shell.CreateShortcut($shortcutPath)
    Assert-QA ($shortcut.TargetPath -eq $installedApp) 'Start menu shortcut targets the wrong application.'
    $originalArguments = $shortcut.Arguments
    $shortcut.Arguments = (($arguments | ForEach-Object { ConvertTo-AirBridgeArgument $_ }) -join ' ')
    $shortcut.Save()
    try {
        # Exercise Windows shell resolution of the actual installed Start menu link.
        Start-Process -FilePath $shortcutPath -WorkingDirectory $directory -WindowStyle Hidden
        $deadline = [DateTime]::UtcNow.AddSeconds(30)
        $manifest = $null
        do {
            if (Test-Path -LiteralPath $manifestPath) {
                try { $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json } catch { }
            }
            if ($manifest -and $manifest.ready) { break }
            Start-Sleep -Milliseconds 100
        } while ([DateTime]::UtcNow -lt $deadline)
        Assert-QA ($manifest -and $manifest.ready) 'Installed Start menu launch did not become ready.'
        $process = Get-Process -Id $manifest.pid
        Assert-QA ($process.Path -eq $installedApp) 'Session manifest identifies a different executable.'
        # A shell-launched process is not our Process.Start child. Keep its
        # handle open while alive so .NET can read the exit code after it exits.
        $null = $process.Handle
        $ownedChildren = @(Get-CimInstance Win32_Process | Where-Object { $_.ParentProcessId -eq $process.Id } | ForEach-Object { Get-Process -Id $_.ProcessId -ErrorAction SilentlyContinue })
        $state = & (Join-Path $PSScriptRoot 'debug.ps1') -PipeName $pipe -Method state
        Assert-QA $state.result.ready 'Installed app debug handshake failed.'
        $state | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath (Join-Path $directory 'state.json') -Encoding UTF8
        & (Join-Path $PSScriptRoot 'debug.ps1') -PipeName $pipe -Method snapshot -Parameters (@{surface='flyout';path=(Join-Path $directory 'flyout.png')} | ConvertTo-Json -Compress) | Out-Null
        & (Join-Path $PSScriptRoot 'debug.ps1') -PipeName $pipe -Method shutdown | Out-Null
        Assert-QA ($process.WaitForExit(15000) -and $process.ExitCode -eq 0) 'Installed app did not exit gracefully.'
        foreach ($child in $ownedChildren) {
            try { Assert-QA ($child.WaitForExit(10000)) 'Installed app left an owned child process running.' }
            finally { $child.Dispose() }
        }
        $final = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
        Assert-QA (-not $final.ready) 'Shutdown left the app ready.'
        $run.checks.Add([ordered]@{ name = $Name; status = 'passed'; mode = $state.result.mode; pid = $process.Id; exitCode = $process.ExitCode; gracefulExit = $true }) | Out-Null
    }
    finally {
        $shortcut.Arguments = $originalArguments
        $shortcut.Save()
        if ($process) {
            if (-not $process.HasExited) {
                try { & (Join-Path $PSScriptRoot 'debug.ps1') -PipeName $pipe -Method shutdown | Out-Null } catch { }
                if (-not $process.WaitForExit(5000)) { Stop-AirBridgeOwnedProcess $process }
            }
            $process.Dispose()
        }
    }
}

function Test-BundleUI {
    Add-Type -AssemblyName UIAutomationClient
    Add-Type -AssemblyName UIAutomationTypes
    Add-Type -TypeDefinition @'
using System;
using System.Text;
using System.Runtime.InteropServices;
public static class InstallerQaNativeButton {
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetClassName(IntPtr hwnd, StringBuilder text, int count);
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool PostMessage(IntPtr hwnd, uint message, UIntPtr wparam, IntPtr lparam);
}
'@
    function Invoke-ObservedButton($Button) {
        $pattern = $null
        if ($Button.TryGetCurrentPattern([Windows.Automation.InvokePattern]::Pattern, [ref]$pattern)) {
            ([Windows.Automation.InvokePattern]$pattern).Invoke()
            return
        }
        # WiX's native standard buttons can appear as UIA panes without Invoke.
        # Use only the handle returned for this observed, enabled button.
        $handle = [IntPtr]$Button.Current.NativeWindowHandle
        $class = [Text.StringBuilder]::new(256)
        [InstallerQaNativeButton]::GetClassName($handle, $class, $class.Capacity) | Out-Null
        Assert-QA ($handle -ne [IntPtr]::Zero -and $class.ToString() -eq 'Button') 'Observed setup control is not a native Button.'
        Assert-QA ([InstallerQaNativeButton]::PostMessage($handle, 0x00F5, [UIntPtr]::Zero, [IntPtr]::Zero)) 'Native setup button invocation failed.'
    }
    $process = Start-Process -FilePath $bundle -ArgumentList ('/norestart /log "' + (Join-Path $run.outputDirectory 'bundle-ui.log') + '"') -PassThru -WindowStyle Hidden
    try {
        $deadline = [DateTime]::UtcNow.AddSeconds(60)
        $window = $null
        $install = $null
        do {
            Assert-QA (-not $process.HasExited) 'Setup exited before showing its install UI.'
            $ownedIds = @($process.Id)
            foreach ($level in 1..3) {
                $parents = $ownedIds
                $ownedIds += @(Get-CimInstance Win32_Process | Where-Object { $_.ParentProcessId -in $parents } | Select-Object -ExpandProperty ProcessId)
            }
            $windows = [Windows.Automation.AutomationElement]::RootElement.FindAll([Windows.Automation.TreeScope]::Children, [Windows.Automation.Condition]::TrueCondition)
            foreach ($candidate in $windows) {
                if ($candidate.Current.ProcessId -notin $ownedIds) { continue }
                $condition = [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::NameProperty, 'Install')
                $button = $candidate.FindFirst([Windows.Automation.TreeScope]::Descendants, $condition)
                if ($button -and $button.Current.IsEnabled) { $window = $candidate; $install = $button; break }
            }
            if ($install) { break }
            Start-Sleep -Milliseconds 250
        } while ([DateTime]::UtcNow -lt $deadline)
        Assert-QA ($null -ne $install) 'Setup Install button was not available on the runner desktop.'
        $run.bundleWindowTitle = $window.Current.Name
        $elements = $window.FindAll([Windows.Automation.TreeScope]::Descendants, [Windows.Automation.Condition]::TrueCondition)
        @($elements | ForEach-Object { [ordered]@{name=$_.Current.Name;type=$_.Current.ControlType.ProgrammaticName;enabled=$_.Current.IsEnabled;handle=$_.Current.NativeWindowHandle} }) | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $run.outputDirectory 'bundle-ui.json') -Encoding UTF8
        Invoke-ObservedButton $install
        $deadline = [DateTime]::UtcNow.AddSeconds(180)
        $closed = $false
        do {
            if ($process.HasExited) { break }
            try {
                $texts = @($window.FindAll([Windows.Automation.TreeScope]::Descendants, [Windows.Automation.Condition]::TrueCondition) | ForEach-Object { $_.Current.Name })
                if ($texts -match 'success') {
                    $close = $window.FindFirst([Windows.Automation.TreeScope]::Descendants, [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::NameProperty, 'Close'))
                    if ($close) { Invoke-ObservedButton $close; $closed = $true }
                }
            } catch [Windows.Automation.ElementNotAvailableException] { }
            Start-Sleep -Milliseconds 250
        } while ([DateTime]::UtcNow -lt $deadline -and -not $closed)
        Assert-QA ($process.WaitForExit(10000) -and $process.ExitCode -in 0,3010) 'Setup UI installation did not complete successfully.'
        $run.checks.Add([ordered]@{name='bootstrapper-ui-install';status='passed';exitCode=$process.ExitCode}) | Out-Null
    }
    finally {
        if (-not $process.HasExited) { Stop-AirBridgeOwnedProcess $process }
        $process.Dispose()
    }
}

try {
    Assert-QA (@(Get-AirBridgeProducts -IncludeBundle).Count -eq 0 -and -not (Test-Path -LiteralPath $installedApp)) 'Runner was not clean; refusing to replace an existing AirBridge installation.'
    foreach ($asset in $provenance.assets) {
        Assert-QA ((Get-FileHash -LiteralPath (Join-Path $ArtifactDirectory $asset.name)).Hash.ToLowerInvariant() -eq $asset.sha256) ('Release artifact checksum mismatch: ' + $asset.name)
    }
    Remove-Item Env:OPENAI_API_KEY, Env:AIRBRIDGE_RUN_HARDWARE_TESTS, Env:AIRBRIDGE_MODEL_EVALS -ErrorAction SilentlyContinue
    New-Item -ItemType Directory -Path (Split-Path -Parent $settingsPath) -Force | Out-Null
    $seed = '{"themeMode":"dark","receiverVolumes":{"installer-qa":47},"aiEnabled":false,"restorePreviousRoute":false}'
    [IO.File]::WriteAllText($settingsPath, $seed)
    Invoke-Msi 'clean-msi-install' '/i' $msi
    Assert-InstalledPayload 'clean-msi-payload'
    $ping = Test-AirBridgeHostPing $run $installedHost 'installed-host-ping'
    Assert-QA ($ping.status -eq 'passed') 'Installed RAOP host handshake failed.'
    Test-InstalledApp 'clean-start-menu-launch'
    Invoke-Msi 'clean-msi-uninstall' '/x' $msi
    Assert-Uninstalled 'clean-uninstall-state'
    Test-BundleUI
    Assert-InstalledPayload 'bundle-installed-payload'
    Test-InstalledApp 'bundle-start-menu-launch'
    $bundleUninstall = Invoke-AirBridgeCheck $run 'bundle-uninstall' $bundle @('/uninstall', '/quiet', '/norestart', '/log', (Join-Path $run.outputDirectory 'bundle-uninstall.log')) 180
    Assert-QA ($bundleUninstall.status -eq 'passed') 'Setup bundle uninstall failed.'
    Assert-Uninstalled 'bundle-uninstall-state'
    Invoke-Msi 'prior-version-install' '/i' $PriorMsi
    $oldProducts = @(Get-AirBridgeProducts)
    Assert-QA ($oldProducts.Count -eq 1 -and $oldProducts[0].version -eq '1.0.2') 'Upgrade baseline is not v1.0.2.'
    [IO.File]::WriteAllText($settingsPath, $seed)
    Invoke-Msi 'prior-version-upgrade' '/i' $msi
    Assert-InstalledPayload 'upgrade-installed-payload'
    Assert-QA ([IO.File]::ReadAllText($settingsPath) -eq $seed) 'Upgrade changed existing user settings.'
    Test-InstalledApp 'upgraded-start-menu-launch'
    $preserved = Get-Content -LiteralPath $settingsPath -Raw | ConvertFrom-Json
    Assert-QA ($preserved.themeMode -eq 'dark' -and $preserved.receiverVolumes.'installer-qa' -eq 47) 'App launch did not preserve supported settings.'
    $run.checks.Add([ordered]@{name='upgrade-settings-preservation';status='passed'}) | Out-Null
    Invoke-Msi 'downgrade-rejection' '/i' $PriorMsi -ExpectDowngrade
    Assert-InstalledPayload 'post-downgrade-payload'
    Invoke-Msi 'repair' '/fa' $msi
    Assert-InstalledPayload 'repair-installed-payload'
    Invoke-Msi 'final-uninstall' '/x' $msi
    Assert-Uninstalled 'final-uninstall-state'
    $run.userSettingsRetained = Test-Path -LiteralPath $settingsPath
}
catch { $run.checks.Add([ordered]@{name='installer-lifecycle';status='failed';error=$_.Exception.Message}) | Out-Null }
if (-not (Complete-AirBridgeRun $run)) { exit 1 }
