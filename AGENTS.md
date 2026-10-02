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
