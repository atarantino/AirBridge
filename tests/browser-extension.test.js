const test = require("node:test");
const assert = require("node:assert/strict");
const Core = require("../src/AirBridge.BrowserExtension/sync-core.js");

function bitmap(id, closed) {
  return { id, close: () => closed.push(id) };
}

test("delay is validated while actual buffering remains capped separately", () => {
  assert.equal(Core.clampDelay(undefined), 2000);
  assert.equal(Core.clampDelay(-20), 0);
  assert.equal(Core.clampDelay(6000), 5000);
  assert.equal(Core.MAX_BUFFER_MS, 4000);
});

test("frame queue returns the newest due frame and closes skipped bitmaps", () => {
  const closed = [];
  const queue = new Core.BoundedFrameQueue(4000, 450);
  queue.push({ bitmap: bitmap("a", closed), capturedAt: 100 });
  queue.push({ bitmap: bitmap("b", closed), capturedAt: 200 });
  queue.push({ bitmap: bitmap("c", closed), capturedAt: 900 });
  const due = queue.takeLatestDue(2300, 2000);
  assert.equal(due.bitmap.id, "b");
  assert.deepEqual(closed, ["a"]);
  assert.equal(queue.length, 1);
});

test("queue bounds memory by frame count and age", () => {
  const closed = [];
  const queue = new Core.BoundedFrameQueue(100, 2);
  queue.push({ bitmap: bitmap("a", closed), capturedAt: 0 });
  queue.push({ bitmap: bitmap("b", closed), capturedAt: 50 });
  queue.push({ bitmap: bitmap("c", closed), capturedAt: 200 });
  assert.deepEqual(closed, ["a", "b"]);
  assert.equal(queue.length, 1);
});

test("pause shifts queued timestamps so the delay clock freezes", () => {
  const queue = new Core.BoundedFrameQueue();
  queue.push({ bitmap: {}, capturedAt: 100 });
  queue.shiftClock(500);
  assert.equal(queue.takeLatestDue(2500, 2000), null);
  assert.ok(queue.takeLatestDue(2600, 2000));
});

test("capture dimensions preserve aspect ratio and downscale", () => {
  assert.deepEqual(Core.captureSize(3840, 2160), { width: 1920, height: 1080 });
  assert.deepEqual(Core.captureSize(1920, 1080), { width: 1920, height: 1080 });
  assert.deepEqual(Core.captureSize(320, 180), { width: 320, height: 180 });
});

test("capture dimensions account for device pixel ratio without upscaling", () => {
  globalThis.devicePixelRatio = 0.5;
  assert.deepEqual(Core.captureSize(1920, 1080), { width: 960, height: 540 });
  globalThis.devicePixelRatio = 2;
  assert.deepEqual(Core.captureSize(1280, 720), { width: 1280, height: 720 });
  delete globalThis.devicePixelRatio;
});

test("YouTube player overlay stays below controls while viewport fallback remains topmost", () => {
  const video = { left: 120, top: 80, width: 1280, height: 720 };
  const player = { left: 100, top: 50 };

  assert.deepEqual(Core.overlayLayout(video, player), {
    position: "absolute",
    left: "20px",
    top: "30px",
    width: "1280px",
    height: "720px",
    zIndex: "1",
  });
  assert.equal(Core.overlayLayout(video).position, "fixed");
  assert.equal(Core.overlayLayout(video).zIndex, "2147483646");
});

test("site keys are origin scoped", () => {
  assert.equal(Core.siteKey("https://www.youtube.com/watch?v=1"), "https://www.youtube.com");
});

test("YouTube host matching rejects lookalike domains", () => {
  assert.equal(Core.isYouTubeHostname("youtube.com"), true);
  assert.equal(Core.isYouTubeHostname("www.youtube.com"), true);
  assert.equal(Core.isYouTubeHostname("WWW.YOUTUBE.COM"), true);
  assert.equal(Core.isYouTubeHostname("notyoutube.com"), false);
  assert.equal(Core.isYouTubeHostname("youtube.com.example.org"), false);
});
