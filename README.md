# AirBridge for Windows

AirBridge sends live Windows audio to AirPlay speakers. It can capture the complete Windows system mix or one application's process tree, normalize the stream to 44.1 kHz signed 16-bit stereo PCM, and route it to one or more independently controlled receivers. AirBridge discovers receivers on the local network and does not assume a default speaker.

The normal Windows 10/11 app uses one receiver-first tray flyout for daily controls, plus an expanded Settings window for audio defaults, speaker synchronization, troubleshooting, and the optional assistant. It supports multi-speaker selection, independent speaker volume, acoustic delay measurement, per-speaker alignment trims, and silence standby. Group playback uses independent RAOP sessions: AirBridge anchor-aligns their sender input and can correct constant receiver latency with manual per-speaker trims. It is not AirPlay 2 buffered-mode multi-room synchronization and does not use a shared PTP clock or anchor-time playback.

A larger dashboard remains as a developer preview and diagnostics harness; it is not part of the normal tray workflow. Launch it with `dotnet run --project src\AirBridge.App\AirBridge.App.csproj -- --preview`. The dashboard exposes the same receiver controls plus stream telemetry, assistant controls, and a diagnostics menu used during development and validation.

## How it works

```text
Windows render engine
  → WASAPI system or process-tree loopback
  → stateful 44.1 kHz s16le stereo normalizer
  → shared in-memory PCM fanout
  → one shared 20 ms pump, bounded per-receiver ring, and start gate
  → per-receiver alignment trim and current-user named pipe
  → live pyatv AudioSource and independent RAOP session
  → AirPlay receiver
```

No temporary media file is created anywhere in the streaming path. Raw audio, receiver addresses, credentials, executable paths, and window titles are never sent to OpenAI. Push-to-talk keeps its short recording in memory and sends it only when the user deliberately records a command. Acoustic delay measurement also keeps microphone PCM in memory and never sends it to OpenAI.

## Requirements and setup

- Windows 10 22H2 (build 19045) or newer, or Windows 11
- .NET 9 SDK
- Python 3.12
- An AirPlay/RAOP receiver on the same trusted Private network

```powershell
py -3.12 -m venv .venv
.\.venv\Scripts\python.exe -m pip install -r src\AirBridge.RaopHost\requirements.txt
dotnet run --project src\AirBridge.App\AirBridge.App.csproj
```

On first launch, refresh the output list, select one or more of your discovered speakers, and choose **Start**. New receivers begin at 30% volume, applied only after the RAOP RECORD transition.

Open **Settings** from the gear in the tray flyout. The tray right-click menu also includes **Settings** for keyboard access. Appearance, the default audio source, speaker groups, silence standby, per-speaker sync offsets, troubleshooting, and the optional assistant are organized there. Use **Settings → Groups** to create, rename, edit, or remove groups. The **Groups** button in the tray flyout applies a group's saved speaker selection; choose **Start** separately to begin playback. The same menu links directly to the Groups settings page.

To align multiple speakers, open **Settings → Speaker sync** and add a 0–500 ms delay to whichever speaker plays earlier. Recheck the offsets after moving a speaker or changing the selected outputs; a receiver added to an already-playing stream joins at the live edge. Saved speaker trims are ignored while only one speaker is active and automatically take effect again when the route has multiple speakers.

The five chirps enter in memory through the normalized capture fanout on the same 20 ms sender clock, so their measured medians include the bounded rings, alignment trim, named pipe, RAOP session, receiver latency, and room acoustic return. While measuring, synthetic chirp blocks temporarily replace concurrent capture blocks at one-times rate and can advance even when a silent endpoint emits no callback. No calibration or microphone media file is created.

Silence standby is enabled by default after 60 seconds and is configurable from Settings (10–600 seconds or off) or the GPT tools. It releases all RAOP sessions so other senders can use the speakers while Windows capture stays ready. When real audio returns, AirBridge clears old queued PCM, restarts the same group through the shared gate, and reapplies saved trims and volumes. The reconnect handshake takes roughly 1–3 seconds and audio during that interval is intentionally not replayed.

An OpenAI API key is optional. Paste it into **Settings** to save it for the current Windows user in Windows Credential Manager; the app never displays a saved key. `OPENAI_API_KEY` remains available as a managed override and takes precedence when set:

```powershell
$env:OPENAI_API_KEY = "your-key"
dotnet run --project src\AirBridge.App\AirBridge.App.csproj
```

Core capture, streaming, tray controls, health telemetry, and delay measurement work without an API key.

## Browser picture delay

Load `src/AirBridge.BrowserExtension` as an unpacked Manifest V3 extension in Chrome or Edge. Open its popup on a video site, enable that site, and enter the delay reported by **Measure delay** in AirBridge.

The extension leaves the real `<video>` playing so its audio continues into WASAPI. It makes only the real picture transparent, captures downscaled frames into a bounded in-memory queue, and renders delayed frames on an overlay canvas. Buffering is capped at approximately four seconds and 450 frames. Seeks flush the queue; pause freezes its clock; resize, fullscreen, SPA navigation, and replaced video elements are tracked. DRM/EME or otherwise protected frames are restored and skipped when capture is blocked.

## Runtime logs

AirBridge keeps bounded rolling runtime logs in `%LOCALAPPDATA%\AirBridge\logs`. Open them from **Settings → Advanced → Open logs folder**. The logs include receiver state changes, RAOP subprocess stderr, and exception stack traces. Network addresses, hardware addresses, and pipe identifiers are redacted, and audio is never logged.

## AI Activity Inspector

Open **Settings → Advanced → Open AI Activity Inspector** to see the optional assistant work in real time. When using the developer dashboard in `--preview` mode, the same inspector is available from its diagnostics menu. The bounded, in-memory timeline separates transcription, Responses API requests, local policy decisions, tool calls, tool results, and final answers. It shows model names, request latency, token usage when returned by the API, and whether a local action was allowed, blocked, or awaiting confirmation. Pause, clear, auto-scroll, transcript visibility, and sanitized JSON copy controls are included.

The inspector never stores microphone audio or displays API keys. Credentials, network addresses, hardware identifiers, and pipe identifiers are redacted before an event enters its bounded buffer. Transcript text is hidden in the detail pane unless the user explicitly enables it, and inspector events are not written to disk.

## Verify and package

```powershell
dotnet test AirBridge.sln -c Release
.\.venv\Scripts\python.exe -m unittest discover -s src\AirBridge.RaopHost -p "test_*.py" -v
node --test tests\browser-extension.test.js
.\scripts\package.ps1
```

Machine-level WASAPI tests are deliberately opt-in so ordinary CI cannot hang on a driver COM call:

```powershell
$env:AIRBRIDGE_RUN_HARDWARE_TESTS = "1"
dotnet test AirBridge.sln -c Release --filter "Category=Hardware"
```

Packaging produces a self-contained application and MSI under the gitignored `artifacts/` directory.

## How this was built

AirBridge was developed collaboratively with Codex. Codex accelerated repository exploration, parallel specification and implementation audits, pyatv protocol tracing, focused regression creation, UI iteration with rendered snapshots, hardware telemetry analysis, and repeatable packaging. Human direction supplied the product requirements, receiver environment, network-permission diagnosis, acoustic observations, and the final scope and safety decisions.

The most important design decisions were:

- **File-free PCM:** live audio stays in bounded memory from WASAPI through per-receiver named pipes. There is no WAV, temporary media file, or unbounded queue in the streaming path.
- **A live pyatv source:** AirBridge supplies a permanently open `AudioSource` at pyatv's source-opening seam, bypassing the decoder path that expects a finite file or URL.
- **Process-tree capture:** Windows process-loopback activation captures one application and its descendants without changing the user's default output device.
- **Independent receiver legs:** one normalized capture feeds bounded queues and RAOP sessions per receiver, so a stalled speaker cannot block healthy speakers.
- **One sender clock and a readiness gate:** a shared 20 ms pump keeps every handshake fed with silence, drops connection-time history at the live edge, then sends the first live block to all ready group legs on the same iteration. Per-pipe bounded writers prevent a non-reading receiver from blocking its siblings. This bounds sender-anchor skew to one block.
- **Measured receiver alignment:** acoustic medians reveal latency that RAOP does not report. AirBridge delays faster legs with fixed, frame-aligned silence trims; all legs then remain on one sender clock and each receiver disciplines itself to its own session sync packets.
- **Silence standby:** sustained near-silence releases receivers without discarding the logical route. The first active block triggers a gated live-edge restart, accepting the honest reconnect gap instead of replaying stale audio.
- **Post-RECORD safe volume:** initial volume is carried into pyatv's post-RECORD streaming hook, preventing a receiver's manual volume from winning a session-start race.
- **Picture-only browser sync:** pausing a video cannot correct AirPlay lip sync because it delays both the picture and the audio being captured. AirBridge therefore leaves audio playback untouched and delays only rendered video frames.
- **Local-first AI boundaries:** GPT-5.6 can call a strict, allowlisted operational tool set, but it never receives audio or receiver network details; microphone actions require explicit confirmation.

More detail is available in [architecture](docs/architecture.md), [browser sync](docs/browser-sync.md), [AI tools and privacy](docs/ai-tools.md), [hardware validation](docs/hardware-validation.md), and the [specification audit](docs/spec-audit.md).

## License

AirBridge is available under the [MIT License](LICENSE).
