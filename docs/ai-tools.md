# GPT-5.6 tools, policy, and privacy

All reasoning, intent resolution, diagnosis, and function calling uses the `gpt-5.6` alias through the Responses API. The only exception is user-approved push-to-talk transcription, which uses the dedicated `gpt-4o-transcribe` model. GPT-5.6 itself does not accept audio input.

The tool catalog uses strict JSON schemas with `additionalProperties: false`. A local allowlist independently classifies each call as read-only, reversible, confirmation-required, or forbidden. Unknown tools are rejected. The model never receives a shell, registry, arbitrary file-write, credential-read, firewall, driver, or process-termination capability.

Read-only telemetry includes the current route, buffer fill percentage, underruns, overruns, producer-idle padding, true active starvation, stream state, capture discontinuities, and explicitly unavailable receiver metrics. It excludes raw PCM, IP addresses, credentials, executable paths, authentication tokens, and window titles.

RAOP receiver identifiers also remain local. GPT tools see short process-local aliases such as `receiver-1`; the controller translates those aliases only after a tool call passes policy validation. Route, buffer, alignment, and measurement results are projected through the same alias boundary.

The confirmation-required `measure_acoustic_delay` tool targets one already-streaming receiver, temporarily floors every non-target receiver, and advances five in-memory chirps from the shared clock into the normalized capture fanout before the receiver rings, trims, pipes, and RAOP sessions. It captures the microphone selected under Settings > Speaker sync into memory, rejects local onsets under 300 ms, restores every prior receiver volume, and returns the median delay. `align_group` uses the same confirmation boundary; a clear direct user request to align speakers authorizes that one tool call and its auto-applied proposal. Pending confirmation remains bound to the exact tool arguments if the agent is recreated, and ordinary approvals such as “sure” or “go ahead” are accepted. Microphone PCM is destroyed locally and never enters a model request.

The diagnosis prompt requires observe → classify → gather → explain → act → verify. A buffer change records the pre-change underrun counter. A later health read waits for the ten-second measurement window when needed, compares fresh counters, and marks the fix verified only when no new underrun occurred and the stream remained in `Streaming`. This makes verification deterministic rather than a prose convention.

Persistent tools require explicit confirmation. When a model requests one, AirBridge presents a local Allow/Cancel dialog; Allow executes that exact tool call immediately, so the user never has to discover a special approval phrase or wait through another model round trip. Streaming remains completely functional when no API key or OpenAI connection is available.

Calibration failures are shown immediately in the main conversation and as a Windows notification, including the locally selected microphone and the speaker that failed. AirBridge records local sample count, capture duration, peak, RMS, and chirp-emission count so it can distinguish an unavailable or silent input from a live microphone whose audio did not contain matching chirps. The same sanitized error is retained in diagnostics; users do not need to open the Activity Inspector or log files to discover a failed operation.

Per-speaker alignment trims support 0–2000 ms. This fits inside the shared five-second PCM buffer and allows AirPlay receivers with substantially different presentation delays to be aligned without silently clipping the proposed correction at 500 ms.

## Activity Inspector

The AI Activity Inspector observes the explicit boundaries around transcription, Responses API turns, policy evaluation, local tool execution, and assistant output. It uses a bounded 250-event in-memory store. Sanitized API, policy, tool, and error events are also written to the bounded rotating `logs/ai-activity.jsonl` diagnostic log so confirmation failures survive an app restart. Transcripts and assistant response text remain memory-only and are never written to that file. Each event is sanitized before it enters either store, and copied JSON respects the transcript-visibility control. Raw microphone audio, authorization headers, full credentials, network addresses, hardware identifiers, and pipe names are never inspector fields.
