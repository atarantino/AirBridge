# Multi-speaker UX and architecture

The normative product requirements are in §22 of the original project specification. AirBridge supports best-effort grouped broadcasting through independent receiver legs. It does not claim sample-accurate AirPlay 2 multi-room synchronization for independently discovered receivers.

The tray flyout is the single device-first control surface: availability, connection state, saved-group selection, per-receiver volume, and a direct play/stop or retry action. The expanded Settings window contains infrequent configuration such as group membership and per-speaker synchronization. The tray right-click menu is intentionally limited to status, start/stop, Settings, and Quit.

One capture route feeds a shared packetization clock and bounded per-receiver queues. Each receiver has a unique named pipe and independently addressable RAOP session. A slow, failed, or reconnecting receiver cannot backpressure or terminate healthy receivers.

Acceptance evidence and outstanding original-spec gaps are tracked in `docs/spec-audit.md`.
