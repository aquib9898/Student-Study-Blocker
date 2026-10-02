// Receives the metadata of the video that's playing from page-bridge.js and
// passes it to the background worker, which classifies it and tells this
// file to resume the player or show the block screen or grey-zone popup
// (rendered by overlay.js's window.SBOverlay, loaded just before this
// file). This file has no classifier or service logic of its own — the
// background worker is the only part that talks to the service.

const TAG = "[Study Blocker]";
const PAGE_SOURCE = "study-blocker-page";

// The video on this page as last reported, so it can be checked again.
let current = null;

// Any script on the page could post a message shaped like this, so it's only
// as trustworthy as the page itself, same as reading the page text would be.
window.addEventListener("message", (event) => {
  if (event.source !== window || event.data?.source !== PAGE_SOURCE) return;
  const meta = event.data.meta;
  if (typeof meta?.id !== "string" || typeof meta.title !== "string") return;
  current = meta;
  // One readable line, with every field the classifier will get tucked
  // underneath it (click the line in DevTools to expand).
  console.groupCollapsed(TAG, `video ${meta.id}: "${meta.title}" by ${meta.channel ?? "unknown channel"}${meta.isShort ? " (Short)" : ""} [${meta.category ?? "no category"}]`);
  console.log("category:", meta.category ?? "(not given)");
  console.log("channel id:", meta.channelId);
  console.log("length:", meta.lengthSeconds, "s", meta.isLive ? "(live)" : "");
  console.log("keywords:", meta.keywords?.length ? meta.keywords.join(", ") : "(none set by the creator)");
  console.log("description:", (meta.description || "(empty)"));
  console.groupEnd();
  report(meta);
});

// Reloading the extension while this tab was already open leaves this
// script running against a torn-down connection: chrome.runtime.sendMessage
// can then throw synchronously ("Extension context invalidated") instead of
// just rejecting. Harmless — the tab gets a fresh, working copy of this
// script on its next load — but worth swallowing so it doesn't show up as
// an error every time.
function report(meta) {
  try {
    chrome.runtime.sendMessage({ type: "video-metadata", meta }).catch(() => {});
  } catch {
    // extension context invalidated; nothing to do from a stale tab.
  }
}

// The video in the address bar, read the same way page-bridge.js does.
function addressVideoId() {
  const url = new URL(location.href);
  if (url.pathname === "/watch") return url.searchParams.get("v");
  const parts = url.pathname.split("/");
  return parts[1] === "shorts" ? parts[2] ?? null : null;
}

// Coming back to this tab (from the desktop app, say) checks the open video
// again, so a change made there meanwhile applies without a reload:
// switching the extension off lifts a block screen, and switching it back
// on, blocking this channel or turning on education-only mode blocks the
// video. At most every few seconds, since focus also bounces between the
// page and its frames.
let lastRecheck = 0;
function recheck() {
  if (!current || current.id !== addressVideoId() || Date.now() - lastRecheck < 3000) return;
  lastRecheck = Date.now();
  report(current);
}
window.addEventListener("focus", recheck);
document.addEventListener("visibilitychange", () => {
  if (!document.hidden) recheck();
});

function player() {
  return document.querySelector("video.html5-main-video, video");
}

// True while the block screen or grey-zone popup is up. The overlay only
// covers the page: YouTube's own keyboard shortcuts (k, space) and the
// keyboard's media keys still reach the player underneath, so a pause done
// once isn't enough. Any play while this is set is paused straight back.
let holdPaused = false;

// "play" doesn't bubble, but a capturing listener on the document still sees
// it for every media element on the page.
document.addEventListener("play", (event) => {
  if (holdPaused && event.target instanceof HTMLMediaElement) {
    event.target.pause();
    console.info(TAG, "kept paused: a block screen or popup is showing");
  }
}, true);

function pause(reason) {
  holdPaused = true;
  const v = player();
  if (v && !v.paused) {
    v.pause();
    console.info(TAG, "paused", reason ? `(${reason})` : "");
  }
}

// Plays the video again only if this file was the one holding it paused,
// so a re-check never starts a video you paused yourself.
function resume() {
  const wasHolding = holdPaused;
  holdPaused = false;
  const v = player();
  if (wasHolding && v && v.paused) {
    v.play().catch(() => {});
    console.info(TAG, "resumed");
  }
}

chrome.runtime.onMessage.addListener((message, _sender, sendResponse) => {
  switch (message?.type) {
    case "resume-video":
      window.SBOverlay?.hide();
      resume();
      sendResponse({ ok: true });
      break;
    case "show-block-screen":
      pause(message.reason);
      window.SBOverlay?.showBlockScreen(message.reason, message.logo);
      sendResponse({ ok: true });
      break;
    case "show-grey-popup":
      pause(message.reason);
      window.SBOverlay?.showGreyPopup(message.reason, message.channelId, message.channelName, (decision) => {
        chrome.runtime.sendMessage({
          type: "answer-grey-popup",
          channelId: message.channelId,
          channelName: message.channelName,
          videoId: message.videoId,
          decision,
        }).catch(() => {});
      });
      sendResponse({ ok: true });
      break;
    // The toolbar popup's Block channel button: the video on screen, if it's
    // still the one in the address bar.
    case "get-current-video":
      sendResponse(current && current.id === addressVideoId()
        ? { id: current.id, channel: current.channel ?? null, channelId: current.channelId ?? null }
        : null);
      break;
    // A channel was just blocked: check the open video again straight away,
    // so it's blocked without waiting for this tab to be focused.
    case "recheck-video":
      if (current && current.id === addressVideoId()) report(current);
      sendResponse({ ok: true });
      break;
    default:
      sendResponse({ ok: false, error: "unknown message" });
  }
});
