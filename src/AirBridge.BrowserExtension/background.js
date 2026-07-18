const HOST = "com.airbridge.sync";

chrome.runtime.onMessage.addListener((message, sender) => {
  if (message.type === "airbridge-status") {
    chrome.storage.local.set({ lastStatus: message.status, lastUpdated: Date.now() });
    return;
  }
  if (message.type === "airbridge-settings-updated") {
    chrome.runtime.sendNativeMessage(HOST, message.settings, () => void chrome.runtime.lastError);
  }
});
