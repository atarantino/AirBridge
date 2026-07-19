const test = require("node:test");
const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const Core = require("../src/AirBridge.FirefoxExtension/sync-core.js");
const manifest = require("../src/AirBridge.FirefoxExtension/manifest.json");

const extensionRoot = path.join(__dirname, "..", "src", "AirBridge.FirefoxExtension");

test("Firefox manifest is MV3, frame-aware, and native-messaging-free", () => {
  assert.equal(manifest.manifest_version, 3);
  assert.equal(manifest.version, "0.3.0");
  assert.deepEqual(manifest.permissions, ["activeTab", "storage"]);
  assert.equal(manifest.content_scripts[0].all_frames, true);
  assert.equal(manifest.background, undefined);
  assert.equal(manifest.browser_specific_settings.gecko.id, "airbridge-video-sync@airbridge.local");
  assert.deepEqual(manifest.browser_specific_settings.gecko.data_collection_permissions.required, ["none"]);
});

test("Firefox scripts use the browser API namespace", () => {
  for (const file of ["content.js", "popup.js"]) {
    const source = fs.readFileSync(path.join(extensionRoot, file), "utf8");
    assert.match(source, /\bbrowser\./);
    assert.doesNotMatch(source, /\bchrome\./);
  }
});

test("Firefox capture and queue limits match the Chrome extension", () => {
  assert.equal(Core.MAX_FRAMES, 450);
  assert.equal(Core.MAX_BUFFER_MS, 4000);
  assert.deepEqual(Core.captureSize(3840, 2160), { width: 1920, height: 1080 });
});
