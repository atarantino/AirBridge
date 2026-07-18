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
  assert.deepEqual(Core.captureSize(1920, 1080), { width: 640, height: 360 });
  assert.deepEqual(Core.captureSize(320, 180), { width: 320, height: 180 });
});

test("site keys are origin scoped", () => {
  assert.equal(Core.siteKey("https://www.youtube.com/watch?v=1"), "https://www.youtube.com");
});
