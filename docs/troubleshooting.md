# Troubleshooting

## Runtime logs

AirBridge keeps bounded rolling logs in `%LOCALAPPDATA%\AirBridge\logs`. The active file is `airbridge.log`; it rotates at approximately 2 MB and retains four older files. Open the directory from **Settings → Advanced → Open logs folder**.

The runtime log includes receiver state changes, RAOP subprocess errors, and exception stack traces. Network addresses, hardware addresses, and named-pipe identifiers are redacted before writing, and raw audio is never logged.

The same directory contains `ai-activity.jsonl`, a bounded diagnostic record of sanitized API, policy, tool, and error events. Transcripts and assistant response text remain memory-only. Credentials, network addresses, hardware identifiers, pipe identifiers, and local paths are redacted from AI activity details. See [AI tools and privacy](ai-tools.md) for the full data boundary.

## No speakers appear

- Confirm that Windows and the AirPlay receiver are on the same trusted network.
- Set the Windows network profile to **Private**; discovery and RAOP setup can fail on a Public profile.
- Refresh the receiver list after changing the network profile or waking a receiver.
- For a Mac receiver, set **Allow AirPlay for** to **Anyone on the Same Network**. **Current User** requires the sender to use the same Apple Account, which a Windows sender cannot satisfy.

## Streaming does not start

- Open the runtime logs and look for the receiver's latest discovery, pairing, SETUP, or RECORD error.
- If a receiver displays a pairing code, complete the prompt in AirBridge and retry.
- Confirm that the receiver is not already reserved by another sender.
- Remember that a new receiver starts at 30% volume; raise the receiver slider if it connects but sounds unexpectedly quiet.

## Push-to-talk does not work

- Add an OpenAI API key under **Settings → Assistant**, leave the assistant enabled, and save.
- Allow microphone access for desktop apps in Windows and confirm that the default input is not muted.
- Hold the configured shortcut—`Ctrl+Alt+Space` by default—for at least 250 ms while speaking, then release it.
- If Windows cannot register the shortcut, set a different modified key combination under **Settings → General**.
- Open **Settings → Advanced → Open AI Activity Inspector** to distinguish microphone, transcription, Responses API, policy, and tool failures.
