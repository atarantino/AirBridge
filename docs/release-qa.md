# Disposable Windows VM release QA

This is a release acceptance gate to run in a disposable Windows 10/11 VM. The packaging job builds artifacts and checks the bundled host; it does not prove install, upgrade or uninstall. No script can infer reliably that the current machine is disposable, so installer execution is deliberately kept as an explicit VM procedure.

## Prepare the VM

1. Snapshot a clean VM with no Python or .NET runtime installed. Use a Windows build supported by AirBridge, an interactive desktop, and an administrative installer session.
2. Copy the new `AirBridge.msi` and `AirBridge-Setup.exe`, the package `report.json`, and an independently saved prior release MSI into `C:\qa`. Record their SHA-256 values, tested git SHA/build version, Windows build, VM image ID and snapshot ID. Confirm the package report's required gate and packaged host ping passed.
3. Start a transcript in `C:\qa\evidence`; preserve verbose MSI logs, screenshots, app session manifests and redacted runtime logs. Keep actual receiver, microphone, and live assistant work in their explicit tiers.

## Clean installation and launch

```powershell
New-Item -ItemType Directory -Path C:\qa\evidence -Force | Out-Null
Start-Transcript -Path C:\qa\evidence\transcript.txt
$install = Start-Process msiexec.exe -ArgumentList '/i "C:\qa\AirBridge.msi" /qn /norestart /L*v "C:\qa\evidence\install.log"' -PassThru -WindowStyle Hidden
if (-not $install.WaitForExit(180000)) { throw 'Installation exceeded three minutes; preserve the VM and MSI log for diagnosis.' }
if ($install.ExitCode -notin 0,3010) { throw "Install failed: $($install.ExitCode)" }
```

Confirm installed files and Start menu shortcut. Launch **AirBridge** through its Start menu shortcut, confirm the tray flyout and Settings open, then quit. Check that both app and owned RAOP host exit and logs show bounded cleanup. Run the packaged host ping using the development helper copied into the VM; use the installed host executable, not the source Python script. A clean-machine host ping and app launch must work without a repository, Python venv or .NET SDK.

Test the setup bootstrapper UI separately: restore the clean VM snapshot, run `AirBridge-Setup.exe`, record the displayed product/version and install result, and repeat Start menu launch/quit. The MSI lifecycle log alone does not exercise the bootstrapper UI.

## Upgrade and settings preservation

1. Restore the clean snapshot. Install the independently saved prior version; create identifiable harmless settings such as theme and default receiver volume. Quit the app and record a redacted settings snapshot.
2. Install the new MSI. Verify a single product entry, expected version, preserved supported settings, no duplicate shortcuts, successful launch and owned-process cleanup. Save the upgrade MSI log and before/after evidence.
3. Check documented downgrade rejection using the saved prior MSI. Keep same-version reinstall/repair separate from a true version upgrade; WiX's current hardcoded version must advance before an upgrade test can prove new-version behavior.

## Uninstall and report

```powershell
$uninstall = Start-Process msiexec.exe -ArgumentList '/x "C:\qa\AirBridge.msi" /qn /norestart /L*v "C:\qa\evidence\uninstall.log"' -PassThru -WindowStyle Hidden
if (-not $uninstall.WaitForExit(180000)) { throw 'Uninstall exceeded three minutes; preserve the VM and MSI log for diagnosis.' }
if ($uninstall.ExitCode -notin 0,3010) { throw "Uninstall failed: $($uninstall.ExitCode)" }
Stop-Transcript
```

Confirm product registration, installed binaries, Start menu shortcut and startup registration follow the intended uninstall behavior. Check no owned app or host processes remain. Record whether user settings/credentials are intentionally retained; do not label retained user data a failed uninstall without an established expectation.

The release report should distinguish `passed`, `failed`, and `skipped` for MSI clean install, bootstrapper UI, Start menu launch, bundled host handshake, graceful exit, prior-version upgrade, settings preservation, downgrade rejection, repair and uninstall. Include artifact hashes, logs, screenshots, product versions and VM snapshot provenance. A skipped or failed required lifecycle check leaves release lifecycle acceptance incomplete.
