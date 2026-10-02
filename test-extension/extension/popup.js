const HOST_NAME = "com.smartstudyblocker.test";

const statusEl = document.getElementById("status");

function setStatus(text, cls) {
  statusEl.textContent = text;
  statusEl.className = cls || "";
}

function sendToHost(message, onDone) {
  setStatus("Sending to native host...");
  chrome.runtime.sendNativeMessage(HOST_NAME, message, (response) => {
    if (chrome.runtime.lastError) {
      onDone({ ok: false, error: chrome.runtime.lastError.message });
      return;
    }
    onDone(response);
  });
}

document.getElementById("writeBtn").addEventListener("click", () => {
  const value = new Date().toISOString();
  sendToHost({ action: "write", value }, (result) => {
    if (result && result.ok) {
      setStatus("Wrote:\n" + JSON.stringify(result, null, 2), "ok");
    } else {
      setStatus("Error:\n" + JSON.stringify(result, null, 2), "error");
    }
  });
});

document.getElementById("readBtn").addEventListener("click", () => {
  sendToHost({ action: "read" }, (result) => {
    if (result && result.ok) {
      setStatus("Read:\n" + JSON.stringify(result, null, 2), "ok");
    } else {
      setStatus("Error:\n" + JSON.stringify(result, null, 2), "error");
    }
  });
});
