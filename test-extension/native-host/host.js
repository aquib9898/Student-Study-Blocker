// Native messaging host: speaks Chrome's length-prefixed stdio protocol
// and reads/writes a single harmless test value under HKCU.
//
// Protocol: each message is a 4-byte little-endian length, followed by
// that many bytes of UTF-8 JSON. Same framing both directions.

const { execFile } = require("child_process");

const REG_KEY = "HKCU\\Software\\SmartStudyBlockerTest";
const REG_VALUE_NAME = "TestValue";

let inputBuffer = Buffer.alloc(0);

process.stdin.on("data", (chunk) => {
  inputBuffer = Buffer.concat([inputBuffer, chunk]);
  drainBuffer();
});

process.stdin.on("end", () => process.exit(0));

function drainBuffer() {
  while (inputBuffer.length >= 4) {
    const messageLength = inputBuffer.readUInt32LE(0);
    if (inputBuffer.length < 4 + messageLength) return;

    const messageBytes = inputBuffer.slice(4, 4 + messageLength);
    inputBuffer = inputBuffer.slice(4 + messageLength);

    let message;
    try {
      message = JSON.parse(messageBytes.toString("utf8"));
    } catch (err) {
      sendMessage({ ok: false, error: "Invalid JSON: " + err.message });
      continue;
    }
    handleMessage(message);
  }
}

function sendMessage(obj) {
  const json = Buffer.from(JSON.stringify(obj), "utf8");
  const header = Buffer.alloc(4);
  header.writeUInt32LE(json.length, 0);
  process.stdout.write(Buffer.concat([header, json]));
}

function handleMessage(message) {
  if (message.action === "write") {
    const value = String(message.value ?? "");
    // execFile (no shell) with an argv array avoids shell-injection risk
    // even though this value currently only ever comes from our own popup.
    execFile(
      "reg",
      ["add", REG_KEY, "/v", REG_VALUE_NAME, "/t", "REG_SZ", "/d", value, "/f"],
      (err, stdout, stderr) => {
        if (err) {
          sendMessage({ ok: false, error: stderr || err.message });
        } else {
          sendMessage({
            ok: true,
            action: "write",
            value,
            registryPath: `${REG_KEY}\\${REG_VALUE_NAME}`,
          });
        }
      }
    );
  } else if (message.action === "read") {
    execFile("reg", ["query", REG_KEY, "/v", REG_VALUE_NAME], (err, stdout, stderr) => {
      if (err) {
        sendMessage({ ok: false, error: stderr || "Not set yet. Try Write first." });
      } else {
        const match = stdout.match(/REG_SZ\s+(.*)/);
        sendMessage({ ok: true, action: "read", value: match ? match[1].trim() : stdout.trim() });
      }
    });
  } else {
    sendMessage({ ok: false, error: "Unknown action: " + message.action });
  }
}
