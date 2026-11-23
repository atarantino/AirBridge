# AirBridge agent development

Read `docs/agent-development.md` before changing launch, capture, transport, UI, or packaging behavior.

- Start a fresh checkout/worktree with `scripts/bootstrap.ps1`, then `scripts/doctor.ps1`. These scripts resolve the repository root from their own location and work from another current directory.
- Keep `.venv`, build outputs, runtime profiles, and evidence inside this checkout. Do not copy a virtualenv from another worktree or overwrite another running checkout's outputs.
- Use `scripts/run.ps1` for development. Its default is a hardware-free fixture; `-Live` explicitly launches the production transport. The launcher supplies a per-worktree `AIRBRIDGE_DATA_DIR`. Installed production launches keep their normal user profile.
- Use `scripts/fixture-qa.ps1` to exercise actual controller/UI actions with deterministic capture and receivers. Use `scripts/debug.ps1` only against a pipe belonging to the session you launched.
- Run `scripts/verify.ps1` before reporting the default verification tier passed. Add `-Snapshots` for visual changes and inspect the PNGs. Report failures, inconclusive results and skipped tiers explicitly.
- Never claim a fixture, successful RAOP connection, or WASAPI startup proves audible output. Hardware and acoustic checks require explicit opt-in and reserved receivers; see the verification tiers in the development guide.
- Paid model evals live outside `AirBridge.sln` and default CI. Default scripts strip API/hardware opt-ins from child processes. Do not run live model evals or capture microphone/audio for ordinary offline validation.
- Cleanup only processes owned by the current session, using its debug shutdown command. Do not kill by app name. Preserve the session manifest, logs and reports when a check fails.
- `scripts/package.ps1` runs the same offline gate and checks the packaged host. Installer lifecycle testing belongs in a disposable VM. Do not install on the user's machine merely to validate a build.

Tests span C# (`tests`), Python (`src/AirBridge.RaopHost/test_*.py`), and browser JavaScript (`tests/*extension.test.js`). Changes at a process boundary need the host ping and fixture QA in addition to component tests.

## Fast inner loop

`verify.ps1` takes about 40 s (about 1 min with `-Snapshots`; fixture QA and snapshots are the slow parts). While iterating, run the suite you touched, then finish with `verify.ps1`. Times are warm runs on a dev machine.

```powershell
dotnet build AirBridge.sln -c Release                                                    # ~10 s
dotnet test tests\AirBridge.Tests -c Release --no-build --filter 'Category!=Hardware'     # ~5 s
dotnet test tests\AirBridge.Tests -c Release --no-build --filter 'FullyQualifiedName~PcmNormalizer'
dotnet test tests\AirBridge.Evals -c Release --no-build                                  # ~2 s, offline policy evals
.\.venv\Scripts\python.exe -m unittest discover -s src\AirBridge.RaopHost -p 'test_*.py'  # ~4 s
.\.venv\Scripts\python.exe -m unittest discover -s src\AirBridge.RaopHost -p 'test_host.py' -k Volume
node --test tests\browser-extension.test.js tests\firefox-extension.test.js               # <1 s
.\scripts\fixture-qa.ps1                                                                 # ~20 s, process + UI actions
.\scripts\snapshots.ps1                                                                  # ~17 s, then inspect PNGs
```

`dotnet test AirBridge.sln` already includes `AirBridge.Evals`. Do not run `tests\AirBridge.ModelEvals`.

For a UI change, prefer the deterministic renderers over a live window. For example, `dotnet run --project src\AirBridge.App -c Release -- --snapshot <png> Dark` renders the dashboard. Run `AirBridge.App.exe --help` for the flyout, settings, HUD and activity variants. Each writes a PNG plus a `.layout.json` sidecar. `-- --preview` opens an interactive dashboard window with fixture data and has to be closed by hand.

## Where to look when something fails

- Script runs print a final `FAILED`/`WARNING` summary with log tails. The full record is `artifacts\qa\<kind>-<timestamp>-<id>\report.json`, and each check has `<name>.stdout.log`/`.stderr.log` next to it.
- App and runtime log: `<profile>\logs\airbridge.log`. The Python RAOP host logs only to stderr, and the app copies each line into this file tagged `[raop-stderr]`. Fixture QA profiles live under the verify run's `fixture\<scenario>-<id>\profile`. `run.ps1` writes the profile path to `session.json`. Installed builds use `%LOCALAPPDATA%\AirBridge\logs`.
- A launch without `AIRBRIDGE_DATA_DIR`/`--data-dir` in preview, fixture or diagnostics mode gets a throwaway `%TEMP%\AirBridge\qa\<guid>` profile. Diagnostics prints it as `log_directory`.
- `tools\AirBridge.Diagnostics --help` and `--ping` are safe. Every other mode needs `AIRBRIDGE_RUN_HARDWARE_TESTS=1`. Any mode given a receiver name streams to a real speaker. `--full-pipeline`, which also runs when no command is given, plays a test tone through the PC's default output as well.

## Setup gotchas

- Plain `python -m venv` uses whatever `python` is on PATH, which may not be 3.12. Let `bootstrap.ps1` create `.venv` (`py -3.12`). If `.venv` already has the wrong version, run `bootstrap.ps1 -RecreateVenv`.
- `.node-version` is exact in CI. Locally, a different minor/patch of the same Node major is reported as a warning, not a failure. Do not switch the machine-wide Node (`nvm use`) just to match it; other sessions share it.
- If the hardware WASAPI tests are needed, set `AIRBRIDGE_RUN_HARDWARE_TESTS` only for that one command and remove it in a `finally`, as in `docs/hardware-validation.md`. These tests start loopback capture only and play nothing.

## What cannot be verified offline

No local fake RAOP receiver exists. Fixture mode replaces both WASAPI capture and the Python/pyatv transport inside the app, so offline checks never exercise the Python host → RTSP/RAOP wire path beyond `--ping`. Audible output, real receivers (HomePod, Apple TV pairing, Mac receivers), toolbar invocation and `activeTab` grants in a live Chromium session, Firefox runtime behavior, tray/hotkey/screen-reader behavior, and installer lifecycle all need hardware, a desktop session, or a disposable VM. Report them as skipped. Never imply they passed.
