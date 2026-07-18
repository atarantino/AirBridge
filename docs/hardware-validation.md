# Hardware validation — 2026-07-17

Discovery on the local Wi-Fi found a HomePod mini test receiver running tvOS 26.5 with RAOP pairing reported as `NotNeeded`. TCP port 7000 was reachable and pyatv 0.18.0 discovery passed.

Initial in-memory PCM smoke-test attempts reached RAOP `SETUP` but timed out while the trusted test Wi-Fi profile was `Public`. After the profile was changed to `Private`, the receiver completed transient pairing, RTSP `SETUP`, `RECORD`, event-channel negotiation, and returned its audio/control ports.

A five-second direct in-memory PCM tone then streamed successfully. A second full-path test rendered a tone through Windows, captured it through WASAPI loopback, normalized it, passed it through the bounded ring and named pipe, and sent it through pyatv. The session stayed `Streaming`, accepted 1,665,216 PCM bytes, maintained 820–850 ms of buffered audio, and recorded zero underruns and zero overruns.

That test also exposed and fixed a managed capture bug: NAudio 3 reports the ordinary Windows float mix as `WAVEFORMATEXTENSIBLE`. AirBridge originally recognized only the plain `IeeeFloat` enum, rejected the first real packet, and supplied silence afterward. The capture path now recognizes the extensible IEEE-float and PCM subtype GUIDs.

A subsequent audible test revealed a separate static/noise defect downstream of every producer and buffer: pyatv's `AudioSource.readframes` docstring says frames are little-endian, but all of pyatv's built-in sources pass decoded s16 samples through `_to_audio_samples`, which swaps each 16-bit word on little-endian hosts before AirPlay 2 transmission. AirBridge's custom source originally bypassed that conversion. For example, a quiet sample `0x0156` was sent as bytes `56 01`, which the receiver interpreted as `0x5601`—a near full-scale discontinuity. AirBridge now converts canonical s16le to the exact representation returned by pyatv's built-in sources, with golden-vector and parity regression tests.

After that fix, a full eight-second Windows-loopback tone held the ring at 520–550 ms while accepting data at the expected 176,400 bytes/second. There were no overruns and no accumulating underruns while audio was active. Underruns increased only after the finite test tone ended, when the production silence-insertion path intentionally maintained the RAOP timeline. This rules out producer/consumer clock drift as the cause of the prior static.

A final twelve-second run instrumented pyatv's UDP callbacks and received eight HomePod timing requests, zero retransmit requests, and zero lost-packet requests. The default communications microphone then captured the corrected 523.25 Hz tone from the test receiver. Signal-aware windowing measured 98.63% of captured energy at the expected tone, a 520 Hz dominant FFT bin, and exactly 1046.5 zero crossings per second, classifying the result as a clean tone. A final full Windows loopback run held 860–890 ms of buffered audio with zero underruns and zero overruns for all twelve active seconds. Together these checks verify clean acoustic output and rule out clock drift, firewall-blocked timing traffic, and packet loss.

After the multi-receiver refactor, three additional direct receiver runs (5, 8, and 8 seconds) received 5, 7, and 6 timing requests respectively, again with zero retransmit or lost-packet requests. The last run was captured through an external microphone: 98.43% of the measured energy matched 523.25 Hz and the zero-crossing rate was exactly 1046.5/s. This confirms the persistent HomePod activity light corresponded to audible output, not a silent RAOP session. The acoustic verifier now classifies non-bin-centred tones from exact-tone energy plus crossing frequency; a synthetic regression prevents its former coarse-grid false negative.

Pre-submission validation then started the test receiver ten consecutive times through the complete controller/host path. Every attempt reached `Streaming` with the configured 30% default. The host regression records `RECORD` before the first `SET_VOLUME`, and no pre-RECORD audio-volume API call occurs. A live five-chirp microphone measurement returned 1666, 1646, 1650, 1644, and 1647 ms, for a median end-to-end delay of **1647 ms**. Calibration is now mixed into the normalized capture fanout before the bounded receiver rings, shared pump, trim, named pipe, and RAOP session, so subsequent measurements include that complete streaming path. The earlier 1647 ms observation remains historical evidence from the pre-fanout calibration build and should be re-measured before using it as the current browser picture-delay value.

The final eight-second Windows-loopback run held the bounded ring at 11% fill with zero overruns and zero `starved while active` padding. Sixty milliseconds of startup padding and all padding after the tone ended were correctly classified as benign `producer idle`, demonstrating that the health telemetry does not label ordinary silence as an active starvation defect.

If the connection regresses:

1. Confirm the trusted home Wi-Fi remains Private in Windows Settings.
2. Allow the packaged AirBridge executable and RAOP host on Private networks only.
3. Ensure the target receiver is not part of an unsupported active stereo/multi-room session and restart it if setup times out.
4. Run the documented full-pipeline diagnostic again.

The failure is surfaced as a reconnecting/failed transport state; it does not cause unbounded buffering or stale-audio replay.
