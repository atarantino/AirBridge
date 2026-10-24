const test = require("node:test");
const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const manifest = require("../src/AirBridge.FirefoxExtension/manifest.json");

const extensionRoot = path.join(__dirname, "..", "src", "AirBridge.FirefoxExtension");
const chromeRoot = path.join(__dirname, "..", "src", "AirBridge.BrowserExtension");

test("Firefox manifest is MV3, frame-aware, and native-messaging-free", () => {
  assert.equal(manifest.manifest_version, 3);
  assert.equal(manifest.version, "0.3.0");
  assert.deepEqual(manifest.permissions, ["activeTab", "storage"]);
  assert.equal(manifest.content_scripts[0].all_frames, true);
  assert.equal(manifest.background, undefined);
  assert.equal(manifest.browser_specific_settings.gecko.id, "airbridge-video-sync@airbridge.local");
  assert.deepEqual(manifest.browser_specific_settings.gecko.data_collection_permissions.required, ["none"]);
});

test("Firefox matches the Chrome extension except for the browser API namespace", () => {
  const read = (root, file) => fs.readFileSync(path.join(root, file), "utf8").replace(/\r\n/g, "\n");
  for (const file of ["sync-core.js", "popup.html"]) {
    assert.equal(read(extensionRoot, file), read(chromeRoot, file), file);
  }
  for (const file of ["content.js", "popup.js"]) {
    const firefox = read(extensionRoot, file);
    assert.doesNotMatch(firefox, /\bchrome\./, file);
    assert.equal(firefox, read(chromeRoot, file).replace(/\bchrome\./g, "browser."), file);
  }
});
