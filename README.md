# AirBridge for Windows

AirBridge sends live Windows system audio—or one application's audio—to one or more AirPlay speakers. It runs from the system tray and adds per-speaker volume, multi-speaker alignment, silence standby, browser picture-delay correction, and an optional GPT-5.6 voice assistant.

## Judge quick start

**Supported platform:** Windows 10 22H2 (build 19045) or newer, or Windows 11. Testing requires an AirPlay/RAOP receiver on the same trusted Private network. An OpenAI API key is optional; core audio streaming works without one.

1. Download `AirBridge-Setup.exe` from the [latest GitHub release](https://github.com/atarantino/AirBridge/releases/latest). No build tools are required.
2. Run the installer and launch **AirBridge** from the Start menu. The installer is not code-signed, so Windows SmartScreen may require **More info → Run anyway**.
3. Open the tray flyout, select a discovered speaker, and choose **Start**.
4. Play audio on Windows and confirm that it moves to the selected speaker. Adjust its volume from the flyout.
5. Test push-to-talk using the walkthrough below. This requires a microphone, internet access, and a judge-provided OpenAI API key.

For a second test path, start one speaker, open **Settings → Browser sync**, and choose **Measure delay**. Load the browser extension as described below, enter the measured delay, and enable it on a video site to delay the picture while leaving its audio playing normally.

### Push-to-talk judge walkthrough

1. Open the tray flyout, choose the **Settings** gear, and select **Assistant**.
2. Paste an OpenAI API key, leave **Enable the AirBridge assistant when an API key is available** checked, and choose **Save**. The key is stored for the current Windows user in Windows Credential Manager and is never displayed again.
3. In **Settings → General**, confirm the push-to-talk shortcut. The default global shortcut is **Ctrl+Alt+Space** and it works while AirBridge is in the tray.
4. Hold **Ctrl+Alt+Space** for at least 250 ms. When the HUD says **Listening**, say “What speakers are available?” and release the shortcut. The HUD advances through **Transcribing** and **Thinking**, then shows the answer. This first command is read-only and verifies the microphone, transcription, GPT-5.6, and local tool path.
5. Use the exact receiver name returned by the assistant for an action, for example: “AirPlay all system audio to Kitchen,” “Set Kitchen volume to 50 percent,” or “Stop playing system audio.” AirBridge asks for confirmation when local policy requires it.
6. Open **Settings → Advanced → Open AI Activity Inspector** to verify the transcription event, `gpt-5.6` Responses API request, local policy decision, tool call, result, latency, token usage, and cost estimate.

Press **Escape** while recording to cancel a command. If the HUD reports that the microphone is unavailable or no speech was detected, enable Windows microphone access for desktop apps, confirm that the default input is not muted, and try again. If the shortcut does not register, another application may be using it; click the shortcut field under **Settings → General**, press a different modified key combination, and save.

## What it does

- Captures the full Windows mix or one application's process tree through WASAPI.
- Streams live, file-free PCM to independently controlled AirPlay/RAOP receivers.
- Routes to multiple speakers and compensates for constant receiver latency with acoustic measurement and per-speaker trims.
- Releases idle speaker sessions through silence standby, then reconnects the same route when audio resumes.
- Delays browser video frames to correct AirPlay lip sync without delaying the captured audio.
- Accepts optional voice commands through GPT-5.6 with local policy enforcement and an inspectable activity log.

AirBridge uses independent RAOP sessions, not AirPlay 2 buffered-mode multi-room synchronization. Receiver alignment corrects constant latency but does not provide a shared PTP playback clock.

## How it works

```text
Windows WASAPI loopback
  → 44.1 kHz s16le stereo normalization
  → shared bounded in-memory PCM fanout
  → per-receiver trim, named pipe, and RAOP session
  → AirPlay receiver
```

No temporary media file is created in the streaming path. Raw audio, receiver addresses, credentials, executable paths, and window titles are never sent to OpenAI. See [architecture](docs/architecture.md), [hardware validation](docs/hardware-validation.md), and [AI tools and privacy](docs/ai-tools.md) for details.

## Build from source

Requirements: the [.NET 9 SDK](https://dotnet.microsoft.com/download/dotnet/9.0), Python 3.12, Windows 10/11, and an AirPlay receiver on the same trusted Private network.

```powershell
py -3.12 -m venv .venv
.\.venv\Scripts\python.exe -m pip install -r src\AirBridge.RaopHost\requirements.txt
dotnet run --project src\AirBridge.App\AirBridge.App.csproj
```

On first launch, refresh the output list, select one or more receivers, and choose **Start**. Apple TV pairing prompts for the code displayed on the TV. For a Mac receiver, set **Allow AirPlay for** to **Anyone on the Same Network**.

The larger developer diagnostics dashboard is available with:

```powershell
dotnet run --project src\AirBridge.App\AirBridge.App.csproj -- --preview
```

## Browser extension

- **Chrome or Edge:** load `src/AirBridge.BrowserExtension` as an unpacked Manifest V3 extension.
- **Firefox:** open `about:debugging#/runtime/this-firefox`, choose **Load Temporary Add-on**, and select `src/AirBridge.FirefoxExtension/manifest.json`.

Start exactly one speaker, use **Measure delay** in AirBridge, enter the result in the extension popup, and enable the current video site. Protected DRM/EME video is restored and skipped when frame capture is unavailable. More detail is in [browser sync](docs/browser-sync.md).

## Optional GPT-5.6 assistant

Follow the [push-to-talk judge walkthrough](#push-to-talk-judge-walkthrough) for the shortest end-to-end test. `OPENAI_API_KEY` can be used instead of the Settings field as a managed override.

The assistant uses `gpt-5.6` through the Responses API for reasoning, intent resolution, diagnosis, and function calling. `gpt-4o-transcribe` handles user-approved push-to-talk transcription. A local allowlist independently classifies each tool call as read-only, reversible, confirmation-required, or forbidden.

Open **Settings → Advanced → Open AI Activity Inspector** to view transcription events, Responses API requests, policy decisions, tool calls, results, latency, token usage, and cost estimates. The model never receives raw audio, credentials, or receiver network details.

## Built with Codex and GPT-5.6

AirBridge was built during OpenAI Build Week in July 2026 with Codex as the primary implementation partner across C#, Python, and JavaScript.

Human direction supplied the product requirements, receiver environment, network-permission diagnosis, acoustic observations, and final product, safety, and scope decisions. Codex accelerated the work by:

- tracing pyatv's RAOP internals to find the source-opening seam for a permanently open, file-free `AudioSource`;
- designing and debugging the shared sender clock, bounded receiver queues, readiness gate, and acoustic alignment flow;
- running specification and implementation audits to identify drift between intended and implemented behavior;
- writing focused .NET, Python, and Node regressions for stalled receivers, standby restarts, and calibration feedback loops;
- iterating on the tray UI with rendered snapshots and analyzing real HomePod, Apple TV, and Mac telemetry; and
- building the MSI, portable package, and `AirBridge-Setup.exe` release pipeline.

GPT-5.6 contributed directly to the final product as the optional in-app assistant described above. The key engineering decisions remained local and deterministic: audio stays in bounded memory, a stalled receiver cannot block its siblings, dangerous actions are excluded or require confirmation, and assistant fixes are verified against real telemetry. The [specification audit](docs/spec-audit.md) records additional implementation checks.

## Tests and packaging

```powershell
dotnet test AirBridge.sln -c Release
.\.venv\Scripts\python.exe -m unittest discover -s src\AirBridge.RaopHost -p "test_*.py" -v
node --test tests\browser-extension.test.js
.\scripts\package.ps1
```

Machine-level WASAPI tests are opt-in so ordinary CI cannot hang on a driver COM call:

```powershell
$env:AIRBRIDGE_RUN_HARDWARE_TESTS = "1"
dotnet test AirBridge.sln -c Release --filter "Category=Hardware"
```

Packaging produces `artifacts\AirBridge-Setup.exe`, `artifacts\AirBridge.msi`, and a portable application under `artifacts\publish`.

## License

AirBridge is available under the [MIT License](LICENSE).
