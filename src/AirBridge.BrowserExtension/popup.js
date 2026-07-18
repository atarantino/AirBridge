(async function initializePopup() {
  const [tab] = await chrome.tabs.query({ active: true, currentWindow: true });
  const key = AirBridgeSyncCore.siteKey(tab?.url || "unknown");
  const stored = await chrome.storage.sync.get({ siteSettings: {} });
  const settings = stored.siteSettings[key] || { enabled: false, delayMs: AirBridgeSyncCore.DEFAULT_DELAY_MS };
  const enabled = document.getElementById("enabled");
  const delay = document.getElementById("delay");
  const status = document.getElementById("status");
  document.getElementById("site").textContent = key;
  enabled.checked = Boolean(settings.enabled);
  delay.value = AirBridgeSyncCore.clampDelay(settings.delayMs);

  document.getElementById("save").addEventListener("click", async () => {
    const next = { enabled: enabled.checked, delayMs: AirBridgeSyncCore.clampDelay(delay.value) };
    const siteSettings = { ...stored.siteSettings, [key]: next };
    await chrome.storage.sync.set({ siteSettings });
    if (tab?.id) chrome.tabs.sendMessage(tab.id, { type: "airbridge-sync", active: next.enabled, offsetMs: next.delayMs }, () => void chrome.runtime.lastError);
    chrome.runtime.sendMessage({ type: "airbridge-settings-updated", settings: { active: next.enabled, offsetMs: next.delayMs, site: key } });
    status.textContent = next.enabled ? `Delaying picture by ${Math.min(next.delayMs, 4000)} ms` : "Disabled for this site";
  });
})();
