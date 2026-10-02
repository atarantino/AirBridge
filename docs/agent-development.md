# Agent development and verification

AirBridge's development loop is **bootstrap → doctor → isolated fixture session → targeted changes → verify → inspect evidence → owned-session cleanup**. Every command below works in Windows PowerShell 5.1 or PowerShell 7 and resolves paths relative to its script. Use an absolute script path when working outside the checkout.

## First checkout or worktree

Install the SDK selected by `global.json` (9.0.101, allowing 9.0.1xx patches), Python 3.12 with the Windows `py` launcher, and Node 22.19.0 selected by `.node-version`. Scripts diagnose missing tools; they do not install machine-level software.

```powershell
.\scripts\bootstrap.ps1
.\scripts\doctor.ps1
.\scripts\verify.ps1
```

Bootstrap creates a checkout-local `.venv`, restores the pinned Python dependency closure from `requirements.lock.txt`, restores NuGet packages, and runs `npm ci` against `package-lock.json`. `-Offline` uses already available Python packages and NuGet/npm caches; missing dependencies fail with command logs. Download caches can be shared between worktrees. Virtualenvs, `node_modules`, `bin`/`obj`, profiles, and publish outputs must stay checkout-local.

Doctor records the exact SDK, installed SDKs, Node, Python, dependency consistency, and an actual JSON-line ping through the source Python host. It does not scan the network or capture audio. Keep the `.node-version` and Python lock aligned with CI; refresh locks deliberately after verification.

Each invocation saves `report.json` and command stdout/stderr under `artifacts\qa\<kind>-<UTC timestamp>-<id>`. Reports include git SHA, dirty working-tree status, a dirty diff hash including untracked file contents, individual untracked file hashes, tracked diff hash, repository path, run ID, exact executable paths, deadlines, exit codes, and **passed**, **failed**, or **skipped** checks. Use `-OutputDirectory` to choose a destination. Test reports use distinct `AirBridge.Tests.trx` and `AirBridge.Evals.trx` filenames.

## Start and attach to a development session

```powershell
.\scripts\run.ps1                      # default: healthy fixture
.\scripts\run.ps1 -Fixture partial-failure
.\scripts\run.ps1 -Live                # real WASAPI and RAOP transport
.\scripts\run.ps1 -Wait -DurationSeconds 30
```

The launcher builds the exact app path and starts a fixture with a fresh `<run directory>\profile`; live development uses persistent `artifacts\profiles\<worktree-path hash>`. `-DataDirectory` overrides that directory. It supplies `AIRBRIDGE_DATA_DIR`, `AIRBRIDGE_RUN_ID`, and the build commit; removes inherited paid API and hardware-test opt-ins; assigns a unique debug pipe; and waits at most 30 seconds for the owned PID's ready manifest. `-NoBuild` reuses the selected configuration's existing executable.

`session.json` lists the app executable, profile, PID, process start time, pipe name, mode, scenario, readiness, and cleanup command. `app-session.json` records app lifecycle state. Inspect `<profile>\logs` for sanitized app and AI activity logs. An ordinary installed launch continues to use `%LOCALAPPDATA%\AirBridge`; the developer launcher does not reuse that profile's settings, pairing records, or credential namespace.

Use the pipe name from your session manifest:

```powershell
.\scripts\debug.ps1 -PipeName <pipe> -Method state
.\scripts\debug.ps1 -PipeName <pipe> -Method events
.\scripts\debug.ps1 -PipeName <pipe> -Method wait-for-state -Parameters '{"state":"Streaming","timeoutMs":10000}'
.\scripts\debug.ps1 -PipeName <pipe> -Method shutdown
```

The debug interface is opt-in and restricted to the current Windows user. Fixture-only actions use the same app handlers/controller as the UI: start, stop, and volume. Production sessions expose diagnostic state and owned-session shutdown. Requests carry IDs, fail explicitly, and have bounded deadlines. Do not attach to a pipe from a different worktree's session.

The fixture is deterministic capture and RAOP transport feeding the real controller, not an audible receiver test. Fixture QA checks readiness, start/state transitions, accepted PCM and signal, per-speaker volume, stop, and bounded shutdown. Scenarios are `healthy`, `no-receivers`, `partial-failure`, `reconnect`, and `pairing`. See `scripts/fixture-qa.ps1` for executable assertions.

Fixture action examples (replace receiver aliases with the IDs from `state`):

```powershell
.\scripts\debug.ps1 -PipeName <pipe> -Method start -Parameters '{"receiverIds":["fixture-a"]}'
.\scripts\debug.ps1 -PipeName <pipe> -Method volume -Parameters '{"receiverId":"fixture-a","percent":40}'
# Pairing scenario only; Media Room is fixture-b:
.\scripts\debug.ps1 -PipeName <pipe> -Method pair -Parameters '{"receiverId":"fixture-b","code":"1234"}'
.\scripts\debug.ps1 -PipeName <pipe> -Method snapshot -Parameters '{"surface":"flyout","path":"C:\\path\\to\\session\\profile\\flyout.png"}'
```

Snapshot output paths must stay under that session's profile or evidence directory.

```powershell
.\scripts\fixture-qa.ps1 -Configuration Release
.\scripts\host-ping.ps1
.\scripts\host-ping.ps1 -Executable .\artifacts\publish\RaopHost\AirBridge.RaopHost.exe
```

Shutdown using the manifest's command. The launcher kills only its own process tree if initialization or bounded cleanup fails. `-Wait` owns the session for the requested duration and shuts it down on deadline; an ordinary launch leaves the ready session available for debugging. Avoid app-name-based cleanup across parallel worktrees.

## Default verification and visual review

```powershell
.\scripts\verify.ps1
.\scripts\verify.ps1 -Snapshots
.\scripts\snapshots.ps1
.\scripts\test-tooling.ps1
# Optional actual Chromium extension QA; install its browser once:
.\scripts\bootstrap.ps1 -BrowserQA
.\scripts\verify.ps1 -BrowserQA
# Native UI interaction, when WinAppCLI is already available:
.\scripts\verify.ps1 -NativeUI
```

Default verification runs C# component tests, deterministic assistant evals, Python tests, Node extension tests, source Python host ping, the managed C#→Python ping, script quoting/deadline regressions, and fixture process QA. Hardware categories and paid model evals are excluded even if inherited environment variables are set. Native commands have explicit deadlines and preserve their exit codes and logs.

`-BrowserQA` runs the actual unpacked Chromium extension against local generated video fixtures using the pinned Playwright dependency and a fresh browser profile. It checks popup controls, real extension storage, decoded delayed pixels, pause/resume, dynamic video replacement, cleanup, and the four-second delay cap. A QA-only initializer in the copied extension selects the actual fixture video tab for the popup's active-tab query; direct popup navigation does not receive Chrome's user-granted `activeTab` permission. Production popup, storage, content-script, media and canvas APIs remain real. Toolbar invocation and the native `activeTab` grant are unverified, as is Firefox runtime interaction; the attached coverage JSON records these gaps. Its JSON report, screenshots and Playwright traces join the verification run directory. This opt-in is separate from the fast Node core/manifest tests. CI runs Chromium QA in a disposable Linux job and retains its evidence.

`-NativeUI` invokes the optional WinAppCLI interaction harness with `-RequireAvailable`; it fails clearly if the native automation tool is unavailable. Running `ui-qa.ps1` directly without that switch reports unavailable automation as skipped. Neither command installs the CLI. Inspect the native report and screenshots alongside fixture state assertions; native QA uses its own owned fixture process.

Snapshot generation covers light/dark flyouts at 1×/1.5×/2× scale and 100%/150% text size, plus dashboard, settings, activity inspector and HUD states. PNG creation passing means the rendering command succeeded; inspect images for clipping, overlap, contrast, and text-size behavior. Snapshots do not validate real monitor DPI, focus, tray placement, keyboard/mouse interaction, shortcuts or screen readers. These need a native Windows UI automation or manual pass.

Flyout PNGs also have `.png.layout.json` sidecars from the same render. They record control names/types, bounds, client and minimum sizes, margins, device DPI, font size, visibility, table columns and child geometry. They deliberately omit `Text` fields. Inspect these alongside the image when diagnosing clipping; a generated geometry file does not by itself establish visual acceptance.

CI bootstraps and runs the same `verify.ps1 -Snapshots`, preserving reports, logs, TRX, session manifests and PNGs even when verification fails. The manual workflow's package option runs the shared gate, builds the installer, and pings the bundled host. It does not install the package.

## Verification tiers and acceptance evidence

| Tier | Access and acceptance evidence |
| --- | --- |
| Offline component/process | Default verify report; all required suites passed, actual host handshakes, fixture state/PCM assertions, bounded cleanup. |
| Visual/native desktop | PNG review plus optional WinAppCLI accessibility/invoke/input assertions; real tray/hotkey and screen-reader checks on a desktop session. |
| Windows audio | Explicit opt-in, selected audio devices, controlled tone, captured content/format/channel checks, process include/exclude, device transitions; COM startup alone is insufficient. |
| Receiver transport | Explicit opt-in, reserved receiver and Private network; continuously Streaming, current active PCM advancing, no active starvation/overruns, bounded start/stop/reconnect and cleanup. |
| Acoustic output | Selected microphone and reserved receiver; fresh clean-tone measurement, latency/skew/drift thresholds and captured evidence tied to commit. Connection state alone is insufficient. |
| Live assistant | Explicit paid API run with separate profile; transcription→model→policy→tool→observable outcome. Report cost and failures separately from deterministic policy evals. |
| Installer lifecycle | Disposable Windows VM; install, Start menu launch, packaged-host ping, graceful exit, upgrade/settings preservation and uninstall/process cleanup. |

Receiver and acoustic diagnostics share an exclusive machine hardware lease so two worktrees cannot manipulate speakers at once. Hardware diagnostics require `AIRBRIDGE_RUN_HARDWARE_TESTS=1`. Consult `docs/hardware-validation.md` and diagnostics `--help` before running them. They emit JSON criteria; **failed** and **inconclusive** results are not passes, and successful software/transport checks still report acoustic output as unproven.

The optional `scripts/hardware-qa.ps1` and `scripts/model-qa.ps1` wrappers use the same run-scoped evidence format, isolated profiles and process deadlines. Read their explicit opt-in parameters and the selected device/model requirements before invoking them; neither wrapper is part of default verification.

For a hardware change, document the receiver/device versions, controlled input, current build, expected thresholds, observed metrics, skipped checks and cleanup. Historical July validation is useful background; it does not verify the current checkout.

## Packaging and remaining setup improvements

```powershell
.\scripts\package.ps1
```

Packaging bootstraps dependencies, runs the shared default verification gate, publishes .NET and the Python executable, checks the bundled JSON protocol, and builds WiX with an explicit source-root definition. Caller working directory does not select installer inputs. `-SkipTests` is an explicit bypass recorded as skipped verification in the report; it is not release acceptance. Packaging does not install or change startup behavior.

The next useful additions are broader native UI focus/tray/hotkey tests, Firefox extension fixtures, a disposable VM installer job, and acoustic thresholds against a reserved receiver matrix. The release lifecycle procedure is documented in [Disposable Windows VM release QA](release-qa.md).

`.codex/environments/environment.toml` supplies Windows worktree bootstrap and Run fixture, Doctor, Verify, Snapshots, and Browser QA actions. Its format was checked against the installed desktop app's local-environment parser; its TOML is independently parseable. OpenAI documents worktree setup and action behavior in [Local environments](https://learn.chatgpt.com/docs/environments/local-environment). The scripts remain directly usable from ordinary checkouts and PowerShell.
