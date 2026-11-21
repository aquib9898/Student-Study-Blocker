// Test 2: can Chrome start a native-messaging host that lives inside an MSIX?
// Asks each host once, then hands the summary to the control host (a copy
// outside any package), which writes it to out\results.json.
const HOSTS = ["com.ssdb.msix_control", "com.ssdb.msix_direct", "com.ssdb.msix_alias"];

function ask(host) {
  return new Promise((resolve) => {
    chrome.runtime.sendNativeMessage(host, { ping: host }, (reply) => {
      resolve(chrome.runtime.lastError ? { error: chrome.runtime.lastError.message } : { reply });
    });
  });
}

(async () => {
  const results = {};
  for (const host of HOSTS) results[host] = await ask(host);
  chrome.runtime.sendNativeMessage("com.ssdb.msix_control", { results }, () => void chrome.runtime.lastError);
})();
