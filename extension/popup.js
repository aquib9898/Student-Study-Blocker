// Status, plus one control: Block channel, for the YouTube video on screen
// (the user's request, 2026-09-30). Every other setting lives in the desktop
// app, and the extension only enforces them. Blocking only ever makes things
// stricter, so it's offered during a lock too (see blockChannel in
// background.js).

function send(type, fields = {}) {
  return new Promise((resolve) => chrome.runtime.sendMessage({ type, ...fields }, resolve));
}

// Until the lock ends: its own time, or a "Break lock early" countdown that
// finishes first.
function fmtRemaining(lock) {
  if (!lock?.IsLocked) return "none";
  let remainingMs = lock.DurationMs - lock.ElapsedMs;
  if (lock.Break) remainingMs = Math.min(remainingMs, lock.Break.DurationMs - lock.Break.ElapsedMs);
  const mins = Math.round(Math.max(0, remainingMs) / 60000);
  const left = mins < 60 ? `${mins} min left` : `${Math.floor(mins / 60)}h ${mins % 60}m left`;
  return lock.Break ? `${left}, ending early` : left;
}

async function render() {
  const result = await send("get-status");
  const serviceEl = document.getElementById("service");
  const lockEl = document.getElementById("lock");
  const sitesEl = document.getElementById("sites");
  const noteEl = document.getElementById("note");

  if (result?.connected) {
    serviceEl.innerHTML = '<span class="dot ok"></span>connected';
    const lock = result.status.Lock;
    lockEl.textContent = fmtRemaining(lock);
    // Switched off in the app (never during a lock), nothing is blocked,
    // sites included: the service lifts their policy.
    const on = lock.IsLocked || lock.Rules?.ExtensionEnabled !== false;
    const sites = lock.Rules?.BlockedDomains ?? [];
    sitesEl.textContent = on ? `${sites.length}` : "off";
    const notes = [];
    if (!on) {
      notes.push("Switched off in the app: nothing is blocked.");
    } else {
      if (lock.IsLocked || sites.some((s) => s.toLowerCase() === "youtube.com/shorts")) notes.push("Shorts blocked.");
      if (lock.Rules?.EducationOnly) notes.push("Education-only mode on.");
      const declutter = lock.Rules?.Declutter ?? [];
      if (declutter.length) notes.push(`Declutter: ${declutter.length} active.`);
    }
    noteEl.textContent = notes.join(" ");
    await renderChannel(!!lock.IsLocked, on);
  } else {
    serviceEl.innerHTML = '<span class="dot bad"></span>not connected';
    lockEl.textContent = "—";
    sitesEl.textContent = "—";
    noteEl.textContent = result?.error ?? "";
  }
}

// A YouTube video page: /watch, or a Short.
function isVideoPage(url) {
  try {
    const { hostname, pathname } = new URL(url);
    return (hostname === "youtube.com" || hostname.endsWith(".youtube.com"))
      && (pathname === "/watch" || pathname.startsWith("/shorts/"));
  } catch {
    return false;
  }
}

// The video in this window's open tab, as that page's content script last
// saw it: null when the tab isn't a YouTube video, { video: null } when it
// is but its channel couldn't be read.
async function videoOnScreen() {
  const [tab] = await chrome.tabs.query({ active: true, currentWindow: true });
  if (!tab?.id || !isVideoPage(tab.url)) return null;
  // Rejects when the tab has no content script: one opened before the
  // extension was last reloaded.
  const video = await chrome.tabs.sendMessage(tab.id, { type: "get-current-video" }).catch(() => null);
  return { video: video?.channelId || video?.channel ? video : null };
}

async function renderChannel(locked, on) {
  const nameEl = document.getElementById("channel-name");
  const button = document.getElementById("block-channel");
  const noteEl = document.getElementById("channel-note");
  document.getElementById("channel").hidden = false;

  const onScreen = await videoOnScreen();
  if (!onScreen) {
    noteEl.textContent = "Open a YouTube video to block its channel.";
    return;
  }
  const video = onScreen.video;
  if (!video) {
    noteEl.textContent = "Couldn't read this video's channel. Refresh the page, then try again.";
    return;
  }

  nameEl.textContent = nameEl.title = video.channel ?? "This video's channel";
  nameEl.hidden = false;
  const state = await send("channel-decision", { channelId: video.channelId, channelName: video.channel });
  if (state?.decision === "Blocked") {
    noteEl.textContent = "This channel is blocked.";
    return;
  }
  if (state?.decision === "Allowed") noteEl.textContent = "It's on your allowed list.";

  button.hidden = false;
  button.addEventListener("click", async () => {
    button.disabled = true;
    const reply = await send("block-channel", { channelId: video.channelId, channelName: video.channel, videoId: video.id });
    if (reply?.ok) {
      button.hidden = true;
      noteEl.textContent = !on
        ? "Added to your blocked channels. It applies once the extension is switched back on in the app."
        : locked
          ? "Blocked. You can unblock it in the app once the lock ends."
          : "Blocked. You can unblock it in the app.";
    } else {
      button.disabled = false;
      noteEl.textContent = `Couldn't block it: ${reply?.error ?? "no answer from the extension"}`;
    }
  });
}

render();
