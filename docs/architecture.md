# Architecture

```text
Windows render engine
  → WASAPI system or process-tree loopback
  → stateful 44.1 kHz s16le stereo normalizer
  → shared PCM broadcast hub
      → independent bounded five-second ring per receiver
      → one shared 20 ms pump and readiness gate
      → per-receiver frame-aligned trim
      → current-user-only named pipe
      → LivePcmSource (pyatv AudioSource)
      → independent pyatv RAOP session and receiver volume
```

The capture callback never performs network I/O. It normalizes each packet once and fans the same ordered bytes into a fixed-capacity ring for every active receiver. A slow or reconnecting receiver can overrun only its own ring and cannot stall the others. One shared pump services every connected pipe on the same 20 ms clock and enqueues into a bounded two-block writer per pipe; a non-reading client is disconnected instead of blocking the group clock. Before RECORD/post-volume readiness the pump queues silence without consuming live queues. Once all group legs report `Streaming`, the gate drops connection-time history to the newest 20 ms block and every ready leg consumes its first live block in the same iteration. A ten-second timeout releases ready legs and reports stragglers; a late or reconnecting leg also drops history and rejoins at the live edge. Memory is bounded by the number of selected receivers and cannot grow with stream duration.

pyatv's public `stream_file` entry point accepts buffers but normally asks miniaudio to identify and decode a finite media container. AirBridge provides a narrow adapter at the `open_source` seam: `LivePcmSource` is already a canonical raw `AudioSource`, so no header probing, seeking, FFmpeg process, media URL, or temporary file is involved. EOF is returned only when AirBridge deliberately stops or the local IPC endpoint closes.

One RAOP subprocess owns a receiver-scoped session table. It receives typed JSON commands over redirected standard input and emits receiver-tagged responses/events over standard output. Start, stop, reconnect, and volume commands target one receiver; stop-all remains available. Logs go to standard error. Receiver addresses are used locally but replaced with a local alias before reaching the managed UI or model context.

Speaker groups are measured alignment of independent RAOP sessions. Starting a group creates one session per receiver from the common pump clock; this corrects sender-anchor racing. Real receivers add different, stable internal latency that RAOP does not report, so sequential in-memory microphone measurements compute `trim = max(median) - median`, rounded to 10 ms. A trim inserts a fixed, frame-aligned silence prelude when the gate opens. A live positive nudge inserts one bounded slice; a negative nudge drops one slice once. The slowest receiver remains at zero.

Calibration chirps enter in memory at the normalized capture-fanout boundary, before the per-receiver rings, trims, named pipes, and RAOP sessions. During calibration the one shared 20 ms pump advances exactly one synthetic capture block per tick and concurrent WASAPI blocks are omitted; this keeps the fanout at one-times rate and works even when a silent endpoint produces no callbacks. The measured medians therefore include the same downstream streaming path as program audio. During sequential group measurement, every non-target receiver is temporarily set to the volume floor and every prior volume is restored even if measurement fails.

This is explicitly not AirPlay 2 buffered-mode multi-room synchronization: AirBridge has no shared PTP clock and does not schedule a common receiver anchor time. Within a session, relative skew is expected to remain constant because every leg is fed by one sender clock and each receiver disciplines playback to its own RAOP sync packets. Environmental or receiver changes can require a fresh acoustic alignment.

Silence standby classifies each shared-clock block as active when any signed 16-bit sample reaches 10 LSB in magnitude. After the configured 10–600 second quiet interval (60 seconds by default), AirBridge stops normal RAOP sessions but keeps capture, the pump, the route, volumes, and trims. Receiver queues are held at the live edge. The first active block clears stale data and restarts the same group through the readiness gate. The 1–3 second handshake interval is not replayed.

Connection states follow:

```text
Idle → Discovering → Connecting → Buffering → (group gate) → Streaming
                                                    ↓          ↓
                                                  Failed    Standby
                                                               ↓
                                      Connecting → (group gate) → Streaming

Streaming → Degraded/Reconnecting → Streaming | Failed
```

`Standby` is distinct from `Idle`: it retains a logical route and auto-resumes. Reconnect uses jittered 0.5, 1, 2, 5, 10, and 30 second delays and reconnects at the live edge because the PCM ring is bounded.

## Current technical boundaries

- Receiver-side packet loss and retransmission counts are not exposed by pyatv 0.18.0, so the app reports that metric as unavailable instead of inventing it.
- Browser synchronization covers accessible HTML5 media and cannot control DRM, protected surfaces, canvas-only players, or every site's playback-rate logic.
- Native Zoom/Teams video delay is not implemented; the documented delayed-window viewer remains an experiment rather than a universal sync claim.
