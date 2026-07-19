(function initializeAirBridgeSyncCore(global) {
  "use strict";

  const DEFAULT_DELAY_MS = 2000;
  const MAX_CONFIGURED_DELAY_MS = 5000;
  const MAX_BUFFER_MS = 4000;
  const MAX_FRAMES = 450;
  const MAX_CAPTURE_WIDTH = 1920;

  function clampDelay(value) {
    const numeric = Number(value);
    if (!Number.isFinite(numeric)) return DEFAULT_DELAY_MS;
    return Math.min(MAX_CONFIGURED_DELAY_MS, Math.max(0, Math.round(numeric)));
  }

  function siteKey(url) {
    try {
      const parsed = new URL(url);
      return parsed.origin === "null" ? parsed.href : parsed.origin;
    } catch {
      return "unknown";
    }
  }

  function captureSize(videoWidth, videoHeight) {
    if (!videoWidth || !videoHeight) return { width: 0, height: 0 };
    const pixelRatio = Number(global.devicePixelRatio) || 1;
    const scale = Math.min(1, pixelRatio, MAX_CAPTURE_WIDTH / Math.max(1, videoWidth));
    return {
      width: Math.max(1, Math.round(videoWidth * scale)),
      height: Math.max(1, Math.round(videoHeight * scale)),
    };
  }

  function overlayLayout(videoRect, hostRect = null) {
    const embedded = hostRect !== null;
    return {
      position: embedded ? "absolute" : "fixed",
      left: `${videoRect.left - (hostRect?.left || 0)}px`,
      top: `${videoRect.top - (hostRect?.top || 0)}px`,
      width: `${Math.max(0, videoRect.width)}px`,
      height: `${Math.max(0, videoRect.height)}px`,
      // YouTube controls live above the video container. Keeping the delayed
      // picture inside that container prevents it from covering player chrome.
      zIndex: embedded ? "1" : "2147483646",
    };
  }

  class BoundedFrameQueue {
    constructor(maxAgeMs = MAX_BUFFER_MS, maxFrames = MAX_FRAMES) {
      this.maxAgeMs = maxAgeMs;
      this.maxFrames = maxFrames;
      this.frames = [];
    }

    push(frame) {
      this.frames.push(frame);
      this.prune(frame.capturedAt);
    }

    takeLatestDue(now, delayMs) {
      let due = null;
      while (this.frames.length && now - this.frames[0].capturedAt >= delayMs) {
        if (due?.bitmap?.close) due.bitmap.close();
        due = this.frames.shift();
      }
      return due;
    }

    shiftClock(deltaMs) {
      if (!Number.isFinite(deltaMs) || deltaMs <= 0) return;
      for (const frame of this.frames) frame.capturedAt += deltaMs;
    }

    clear() {
      for (const frame of this.frames) frame.bitmap?.close?.();
      this.frames.length = 0;
    }

    prune(now) {
      while (this.frames.length > this.maxFrames ||
             (this.frames.length && now - this.frames[0].capturedAt > this.maxAgeMs)) {
        this.frames.shift().bitmap?.close?.();
      }
    }

    get length() { return this.frames.length; }
  }

  const api = {
    DEFAULT_DELAY_MS,
    MAX_CONFIGURED_DELAY_MS,
    MAX_BUFFER_MS,
    MAX_FRAMES,
    clampDelay,
    siteKey,
    captureSize,
    overlayLayout,
    BoundedFrameQueue,
  };

  global.AirBridgeSyncCore = api;
  if (typeof module !== "undefined" && module.exports) module.exports = api;
})(typeof globalThis !== "undefined" ? globalThis : self);
