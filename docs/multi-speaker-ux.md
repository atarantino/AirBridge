# Multi-speaker UX and architecture

The normative product requirements are in §22 of the original project specification. AirBridge supports best-effort grouped broadcasting through independent receiver legs. It does not claim sample-accurate AirPlay 2 multi-room synchronization for independently discovered receivers.

The dashboard and tray use device-first receiver rows: availability, connection state, selection, per-receiver volume, and a direct play/stop or retry action. A normal tray-icon click opens the compact routing flyout; the right-click menu remains the keyboard fallback.

One capture route feeds a shared packetization clock and bounded per-receiver queues. Each receiver has a unique named pipe and independently addressable RAOP session. A slow, failed, or reconnecting receiver cannot backpressure or terminate healthy receivers.

Acceptance evidence and outstanding original-spec gaps are tracked in `docs/spec-audit.md`.
