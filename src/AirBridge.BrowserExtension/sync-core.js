(function initializeAirBridgeSyncCore(global) {
  "use strict";

  const DEFAULT_DELAY_MS = 2000;
  const MAX_CONFIGURED_DELAY_MS = 5000;
  const MAX_BUFFER_MS = 4000;
  const MAX_FRAMES = 450;
  const MAX_CAPTURE_WIDTH = 640;

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
    const scale = Math.min(1, MAX_CAPTURE_WIDTH / videoWidth);
    return {
      width: Math.max(1, Math.round(videoWidth * scale)),
      height: Math.max(1, Math.round(videoHeight * scale)),
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
    BoundedFrameQueue,
  };

  global.AirBridgeSyncCore = api;
  if (typeof module !== "undefined" && module.exports) module.exports = api;
})(typeof globalThis !== "undefined" ? globalThis : self);
