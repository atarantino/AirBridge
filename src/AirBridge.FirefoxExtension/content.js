(function initializeAirBridgePictureDelay() {
  "use strict";

  const Core = globalThis.AirBridgeSyncCore;
  const FRAME_FAILURE_LIMIT = 2;
  const CAPTURE_RETRY_MS = 250;

  class DelayedVideo {
    constructor(video, delayMs) {
      this.video = video;
      this.configuredDelayMs = Core.clampDelay(delayMs);
      this.delayMs = Math.min(this.configuredDelayMs, Core.MAX_BUFFER_MS);
      this.queue = new Core.BoundedFrameQueue();
      this.canvas = document.createElement("canvas");
      this.context = this.canvas.getContext("2d", { alpha: false, desynchronized: true });
      this.running = false;
      this.capturePending = false;
      this.captureFailures = 0;
      this.captureHandle = null;
      this.drawHandle = null;
      this.pausedAt = null;
      this.lastFallbackCapture = 0;
      this.generation = 0;
      this.originalOpacity = video.style.getPropertyValue("opacity");
      this.originalOpacityPriority = video.style.getPropertyPriority("opacity");
      this.originalAriaHidden = video.getAttribute("aria-hidden");
      this.boundCapture = (now) => this.capture(now);
      this.boundDraw = (now) => this.draw(now);
      this.boundFlush = () => this.flush();
      this.boundPause = () => { this.pausedAt = performance.now(); };
      this.boundPlay = () => this.resumeClock();
      this.boundLayout = () => this.positionCanvas();

      this.canvas.dataset.airbridgePictureDelay = "true";
      Object.assign(this.canvas.style, {
        position: "fixed",
        pointerEvents: "none",
        margin: "0",
        padding: "0",
        border: "0",
        background: "black",
        zIndex: "2147483646",
        display: "none",
      });
      this.canvas.setAttribute("aria-hidden", "true");
    }

    youtubeVideoContainer() {
      return Core.isYouTubeHostname(location.hostname)
        ? this.video.closest(".html5-video-container")
        : null;
    }

    overlayHost() {
      const youtubeContainer = this.youtubeVideoContainer();
      if (youtubeContainer) return { element: youtubeContainer, embedded: true };
      const fullscreen = document.fullscreenElement;
      if (fullscreen && fullscreen !== this.video && fullscreen.contains(this.video))
        return { element: fullscreen, embedded: false };
      return { element: document.body, embedded: false };
    }

    start() {
      if (this.running || !this.context || !this.video.isConnected) return;
      if (this.video.mediaKeys) {
        this.fail("DRM/EME media cannot be frame-captured");
        return;
      }
      this.running = true;
      document.body.appendChild(this.canvas);
      this.video.style.setProperty("opacity", "0", "important");
      this.video.setAttribute("aria-hidden", "true");
      this.video.addEventListener("seeking", this.boundFlush);
      this.video.addEventListener("emptied", this.boundFlush);
      this.video.addEventListener("pause", this.boundPause);
      this.video.addEventListener("play", this.boundPlay);
      this.video.addEventListener("loadedmetadata", this.boundLayout);
      window.addEventListener("resize", this.boundLayout, { passive: true });
      window.addEventListener("scroll", this.boundLayout, { passive: true, capture: true });
      document.addEventListener("fullscreenchange", this.boundLayout);
      this.positionCanvas();
      this.scheduleCapture();
      this.drawHandle = requestAnimationFrame(this.boundDraw);
      if (this.configuredDelayMs > Core.MAX_BUFFER_MS) {
        console.warn(`AirBridge picture delay is capped at ${Core.MAX_BUFFER_MS} ms to bound memory.`);
      }
    }

    updateDelay(delayMs) {
      const configured = Core.clampDelay(delayMs);
      const effective = Math.min(configured, Core.MAX_BUFFER_MS);
      if (configured === this.configuredDelayMs && effective === this.delayMs) return;
      this.configuredDelayMs = configured;
      this.delayMs = effective;
      this.flush();
    }

    scheduleCapture() {
      if (!this.running) return;
      if (typeof this.video.requestVideoFrameCallback === "function") {
        this.captureHandle = this.video.requestVideoFrameCallback(this.boundCapture);
      } else {
        this.captureHandle = requestAnimationFrame(this.boundCapture);
      }
    }

    async capture(now) {
      if (!this.running) return;
      this.captureHandle = null;
      const fallback = typeof this.video.requestVideoFrameCallback !== "function";
      if (fallback && now - this.lastFallbackCapture < 1000 / 30) {
        this.scheduleCapture();
        return;
      }
      this.lastFallbackCapture = now;
      if (!this.video.paused && this.video.readyState >= this.video.HAVE_CURRENT_DATA && !this.capturePending) {
        this.capturePending = true;
        const generation = this.generation;
        try {
          const size = Core.captureSize(this.video.videoWidth, this.video.videoHeight);
          if (size.width && size.height) {
            const bitmap = await createImageBitmap(this.video, 0, 0, this.video.videoWidth, this.video.videoHeight,
              { resizeWidth: size.width, resizeHeight: size.height, resizeQuality: "medium" });
            if (!this.running || generation !== this.generation) bitmap.close?.();
            else {
              this.queue.push({ bitmap, capturedAt: performance.now(), mediaTime: this.video.currentTime });
              this.captureFailures = 0;
            }
          }
        } catch (error) {
          this.captureFailures += 1;
          if (this.captureFailures >= FRAME_FAILURE_LIMIT) {
            this.fail(`frame capture blocked (${error?.name || "unknown error"}); DRM/CORS media is unsupported`);
            return;
          }
          await new Promise((resolve) => setTimeout(resolve, CAPTURE_RETRY_MS));
        } finally {
          this.capturePending = false;
        }
      }
      this.scheduleCapture();
    }

    draw(now) {
      if (!this.running) return;
      if (this.pausedAt === null) {
        const frame = this.queue.takeLatestDue(now, this.delayMs);
        if (frame) {
          if (this.canvas.width !== frame.bitmap.width || this.canvas.height !== frame.bitmap.height) {
            this.canvas.width = frame.bitmap.width;
            this.canvas.height = frame.bitmap.height;
          }
          this.context.drawImage(frame.bitmap, 0, 0, this.canvas.width, this.canvas.height);
          frame.bitmap.close?.();
          this.canvas.style.display = "block";
        }
      }
      this.positionCanvas();
      this.drawHandle = requestAnimationFrame(this.boundDraw);
    }

    resumeClock() {
      if (this.pausedAt === null) return;
      this.queue.shiftClock(performance.now() - this.pausedAt);
      this.pausedAt = null;
    }

    positionCanvas() {
      if (!this.running || !this.video.isConnected) return;
      const rect = this.video.getBoundingClientRect();
      const host = this.overlayHost();
      if (this.canvas.parentElement !== host.element) host.element.appendChild(this.canvas);
      const hostRect = host.embedded ? host.element.getBoundingClientRect() : null;
      Object.assign(this.canvas.style, Core.overlayLayout(rect, hostRect));
      this.canvas.dataset.airbridgeOverlayMode = host.embedded ? "player" : "viewport";
    }

    flush() {
      this.generation += 1;
      this.queue.clear();
      this.canvas.style.display = "none";
    }

    fail(reason) {
      console.warn(`AirBridge sync skipped a video: ${reason}`);
      this.stop();
    }

    stop() {
      if (!this.running && !this.canvas.isConnected) return;
      this.running = false;
      if (this.captureHandle !== null) {
        if (typeof this.video.cancelVideoFrameCallback === "function") this.video.cancelVideoFrameCallback(this.captureHandle);
        else cancelAnimationFrame(this.captureHandle);
      }
      if (this.drawHandle !== null) cancelAnimationFrame(this.drawHandle);
      this.video.removeEventListener("seeking", this.boundFlush);
      this.video.removeEventListener("emptied", this.boundFlush);
      this.video.removeEventListener("pause", this.boundPause);
      this.video.removeEventListener("play", this.boundPlay);
      this.video.removeEventListener("loadedmetadata", this.boundLayout);
      window.removeEventListener("resize", this.boundLayout);
      window.removeEventListener("scroll", this.boundLayout, true);
      document.removeEventListener("fullscreenchange", this.boundLayout);
      this.queue.clear();
      this.canvas.remove();
      if (this.originalOpacity) this.video.style.setProperty("opacity", this.originalOpacity, this.originalOpacityPriority);
      else this.video.style.removeProperty("opacity");
      if (this.originalAriaHidden === null) this.video.removeAttribute("aria-hidden");
      else this.video.setAttribute("aria-hidden", this.originalAriaHidden);
    }
  }

  class PictureDelayController {
    constructor() {
      this.enabled = false;
      this.delayMs = Core.DEFAULT_DELAY_MS;
      this.videos = new Map();
      this.observer = new MutationObserver(() => this.reconcile());
      this.observer.observe(document.documentElement, { childList: true, subtree: true });
    }

    configure(enabled, delayMs) {
      this.enabled = Boolean(enabled);
      this.delayMs = Core.clampDelay(delayMs);
      this.reconcile();
    }

    reconcile() {
      for (const [video, delayed] of this.videos) {
        if (!this.enabled || !video.isConnected) {
          delayed.stop();
          this.videos.delete(video);
        } else delayed.updateDelay(this.delayMs);
      }
      if (!this.enabled) return;
      for (const video of document.querySelectorAll("video")) {
        if (this.videos.has(video) || video.dataset.airbridgeSyncUnsupported === "true") continue;
        const delayed = new DelayedVideo(video, this.delayMs);
        this.videos.set(video, delayed);
        delayed.start();
      }
    }

    stop() {
      this.enabled = false;
      for (const delayed of this.videos.values()) delayed.stop();
      this.videos.clear();
    }
  }

  const controller = new PictureDelayController();
  const ancestorOrigins = location.ancestorOrigins;
  const settingsUrl = window === window.top
    ? location.href
    : ancestorOrigins?.[ancestorOrigins.length - 1] || document.referrer || location.href;
  const key = Core.siteKey(settingsUrl);

  async function loadSiteSettings() {
    const stored = await browser.storage.sync.get({ siteSettings: {} });
    const settings = stored.siteSettings[key] || { enabled: false, delayMs: Core.DEFAULT_DELAY_MS };
    controller.configure(settings.enabled, settings.delayMs);
  }

  browser.storage.onChanged.addListener((changes, area) => {
    if (area === "sync" && changes.siteSettings) loadSiteSettings();
  });
  window.addEventListener("pagehide", () => controller.stop(), { once: true });
  loadSiteSettings().catch((error) => console.warn("AirBridge sync settings could not be loaded", error));
})();
