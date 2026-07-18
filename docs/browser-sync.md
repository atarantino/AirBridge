# Browser synchronization

The Manifest V3 extension delays only the rendered picture. The real HTML5 `<video>` keeps playing at its original rate so its audio continues through Windows loopback and AirPlay. Its picture is made transparent while `requestVideoFrameCallback`—or a `requestAnimationFrame` fallback—captures downscaled `ImageBitmap` frames into a bounded in-memory queue. An overlay canvas draws the newest frame whose age has reached the configured delay.

Settings are stored per site in Chrome sync storage. The configured range is 0–5000 ms; actual buffering is capped at 4000 ms and 450 frames to bound memory. The popup accepts the median reported by AirBridge's **Measure delay** action.

Seeks and source replacement flush the queue. Pausing freezes the displayed frame and shifts queued timestamps on resume. Resize, scroll, fullscreen changes, SPA navigation, and dynamically replaced video elements are reconciled without changing `currentTime`, calling `pause()`, or assigning `playbackRate`.

This picture-only mechanism is required: pausing the real video delays both its picture and the same audio that WASAPI captures, so the relative AirPlay offset cannot change.

DRM/EME and cross-origin media may prohibit frame capture. After repeated capture failure, AirBridge closes queued bitmaps, removes the canvas, restores the video's original opacity/accessibility state, logs an explicit warning, and skips that element. Canvas-only players and protected browser surfaces are also unsupported.

The native messaging host implements Chromium's four-byte little-endian framing. Before distribution, the installed extension ID and native-host executable path must be written into `com.airbridge.sync.json` and registered for Chrome or Edge.
