# GPT-5.6 tools, policy, and privacy

All reasoning, intent resolution, diagnosis, and function calling uses the `gpt-5.6` alias through the Responses API. The only exception is user-approved push-to-talk transcription, which uses the dedicated `gpt-4o-transcribe` model. GPT-5.6 itself does not accept audio input.

The tool catalog uses strict JSON schemas with `additionalProperties: false`. A local allowlist independently classifies each call as read-only, reversible, confirmation-required, or forbidden. Unknown tools are rejected. The model never receives a shell, registry, arbitrary file-write, credential-read, firewall, driver, or process-termination capability.

Read-only telemetry includes the current route, buffer fill percentage, underruns, overruns, producer-idle padding, true active starvation, stream state, capture discontinuities, and explicitly unavailable receiver metrics. It excludes raw PCM, IP addresses, credentials, executable paths, authentication tokens, and window titles.

RAOP receiver identifiers also remain local. GPT tools see short process-local aliases such as `receiver-1`; the controller translates those aliases only after a tool call passes policy validation. Route, buffer, alignment, and measurement results are projected through the same alias boundary.

The confirmation-required `measure_acoustic_delay` tool targets one already-streaming receiver, temporarily floors every non-target receiver, and advances five in-memory chirps from the shared clock into the normalized capture fanout before the receiver rings, trims, pipes, and RAOP sessions. It captures the default microphone into memory, rejects local onsets under 300 ms, restores every prior receiver volume, and returns the median delay. `align_group` uses the same confirmation boundary; a direct user request to align speakers authorizes that one tool call and its auto-applied proposal. Microphone PCM is destroyed locally and never enters a model request.

The diagnosis prompt requires observe → classify → gather → explain → act → verify. A buffer change records the pre-change underrun counter. A later health read waits for the ten-second measurement window when needed, compares fresh counters, and marks the fix verified only when no new underrun occurred and the stream remained in `Streaming`. This makes verification deterministic rather than a prose convention.

Persistent tools require explicit confirmation and are rejected without it. Streaming remains completely functional when no API key or OpenAI connection is available.
