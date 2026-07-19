# AirBridge Video Sync

## Summary

Delays HTML5 video pictures to keep browser video in sync with AirPlay audio routed through AirBridge.

## Description

AirBridge Video Sync compensates for AirPlay audio latency by delaying only the picture in HTML5 video players. The original video keeps playing normally so its audio can continue through AirBridge, while captured video frames are displayed after your configured delay.

Use the extension popup to enable synchronization per site and enter the delay measured by AirBridge. Embedded players and fullscreen video are supported.

Features:

- Per-site enable and delay settings
- Up to 1920-pixel frame capture, adjusted for display pixel density
- Bounded four-second in-memory frame queue
- Embedded-player and fullscreen support
- Correct handling for pause, play, seek, resize, and page navigation
- Automatic opt-out for DRM/EME and other protected video
- No native-messaging dependency

AirBridge Video Sync does not collect or transmit personal data. Captured video frames remain temporarily in memory and are discarded after display or when playback state changes. Site enablement and delay preferences are stored using the browser's synchronized extension storage.

## Permission justifications

- `storage`: Saves the per-site enable switch and picture-delay setting using browser-managed synchronized extension storage.
- `activeTab`: Lets the popup identify the active site's origin when the user opens the extension.
- All-site content-script access: Finds and delays HTML5 video elements on sites the user explicitly enables. This is required for video players and embedded frames across arbitrary streaming sites.

## Data handling

No data is sold, shared, or transmitted to the developer or third parties. Video frames are processed locally in volatile memory. The browser vendor may synchronize extension preferences as part of the user's browser account.
