# Original specification audit

Audit date: 2026-07-17. Evidence is based on the current repository and completed HomePod mini hardware validation. “Partial” means code exists but the full acceptance scope is not proven.

## Competition MVP

| # | Requirement | Status | Evidence / remaining gap |
|---|---|---|---|
| 1 | Windows tray application | Implemented | `MainForm` owns `NotifyIcon`; left-click opens a compact live flyout and right-click builds receiver/group/source menus dynamically. |
| 2 | AirPlay discovery | Implemented | `host.py` scans pyatv services and `PythonRaopClient` projects stable RAOP identifiers. |
| 3 | Whole-system WASAPI loopback | Implemented | `WasapiCaptureService.StartSystemAsync`; verified through a physical test receiver. |
| 4 | Direct in-memory PCM conversion | Implemented | Stateful normalization/resampling and the corrected AirPlay byte-order adapter. 24-in-32 input remains unsupported. |
| 5 | Indefinite live pyatv streaming | Partial | The live source reports EOF only on deliberate stop and has bounded memory; no eight-hour acceptance run yet. |
| 6 | No temporary audio files | Implemented | WASAPI → bounded PCM → named pipe → pyatv; push-to-talk WAV is memory-only. |
| 7 | Start, stop, destination, volume | Implemented | The tray flyout and context menu expose grouped start, stop-all, independent receiver connect/stop, saved selections, and per-receiver volume. |
| 8 | Live health metrics | Partial | Runtime telemetry and GPT tools track fill percentage, underruns, overruns, producer-idle padding, and active starvation. Full protocol latency breakdown, event timeline, and persistent telemetry remain incomplete. |
| 9 | GPT-5.6 text and push-to-talk | Implemented | Responses API uses `gpt-5.6`; microphone transcription is explicit and memory-only. |
| 10 | GPT routing and diagnostic tools | Partial | Strict schemas and policy exist; several original catalog tools and persistent operations are not implemented. |
| 11 | Self-healing with verification | Partial | Buffer-target changes are remeasured after ten seconds. Golden scenarios, rollback, and tracked eval scores remain missing. |
| 12 | Application-specific capture | Partial | Process-tree capture works; Chrome/Spotify/Zoom lifecycle and restart/grace-period acceptance are unproven. |
| 13 | Browser-sync proof of concept | Partial | MV3 picture-only frame delay, bounded queueing, per-site settings, dynamic video handling, and DRM fallback exist. Native-host registration and a live YouTube acceptance run remain incomplete. |
| 14 | Packaged installer | Implemented | Self-contained app, native host, bundled Python RAOP host, and MSI exist. Signing/startup/browser registration remain incomplete. |

Summary: 8 implemented, 6 partial, 0 wholly absent at code-presence level. The full original specification and success criteria are not yet complete.

## Material gaps outside §22

- Reliability evidence: eight-hour stream, long silence/resume, 100 start-stop cycles, network-loss recovery, sleep/wake, endpoint changes, and memory-under-50-MB proof.
- Receiver matrix: only one HomePod mini is hardware-validated; three receiver types are required by the original success criteria.
- Application matrix: Chrome, Edge, YouTube, Zoom, Teams, Spotify, and VLC need end-to-end capture validation.
- AI: recent-event/audio-format/capability/application tools, quality profile controls, persistent routing rules, streaming output, token/rate handling, golden evals, graders, rollback/escalation.
- Persistence/security: theme, selected receivers, saved groups, and receiver volumes are saved. Startup/restore, DPAPI credentials, authenticated IPC token, rotating telemetry, and signing evidence are still missing.
- Browser sync: native host manifest contains placeholders and the installer does not register it.
- Device lifecycle: suspend/resume, endpoint notifications, receiver reboot/IP change, and process restart recovery are incomplete.

## Multi-speaker scope decision

The original first-release non-goal was true AirPlay 2 multi-room synchronization. Spec §22 deliberately adds best-effort grouped broadcasting through independent RAOP receiver legs, saved groups, and independent volume. It does not claim sample-accurate phase alignment unless receivers expose a native synchronized group.

## Evidence already established

- Test-receiver discovery, pairing, SETUP, RECORD, and clean acoustic output.
- Corrected s16 PCM byte order with golden vector and pyatv parity tests.
- Default-microphone verification: 98.63% of captured energy at the intended 523.25 Hz tone and expected zero-crossing rate.
- Twelve active seconds of full Windows loopback with 860–890 ms buffered audio, zero underruns, and zero overruns.
- Eight HomePod timing requests and zero retransmission/lost-packet requests during the instrumented stream.
- Post-refactor receiver verification: three more lossless instrumented streams and microphone capture with 98.43% expected-tone energy and an exact 1046.5/s crossing rate.
- Receiver fanout, failure isolation, group persistence, and targeted host commands are covered by deterministic tests; only one receiver has physical acoustic validation.
