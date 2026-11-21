// Receives the metadata of the video that's playing from page-bridge.js and
// passes it to the background worker, which classifies it and tells this
// file to resume the player or show the block screen or grey-zone popup
// (rendered by overlay.js's window.SBOverlay, loaded just before this
// file). This file has no classifier or service logic of its own — the
// background worker is the only part that talks to the service.
//
// Two kinds of video arrive (see page-bridge.js): the one in the address bar,
// which gets the full-page block screen as before, and "off-page" ones playing
// somewhere else on the page (the miniplayer after "Add to queue", a hover
// preview, a channel trailer). An off-page video can't get a page-wide screen
// (it would cover a page that has nothing to do with it), so a blocked one is
// just stopped in its own player. Verdicts carry the video id, so each lands
// on the right one.

const TAG = "[Study Blocker]";
const PAGE_SOURCE = "study-blocker-page";
const TO_PAGE = "study-blocker-content";

// The address video as last reported, so it can be checked again.
let current = null;
// Off-page videos by id, as last reported (kept small).
const offPage = new Map();
// Off-page videos blocked a moment ago: a hover preview restarts as soon as
// the mouse moves, and asking the service again every time would let a
// second or so play each time. Cleared by any re-check, and per video by a
// Study verdict, so a change made in the app still applies.
const recentlyBlocked = new Map();
const BLOCK_MEMORY_MS = 60_000;

// Any script on the page could post a message shaped like this, so it's only
// as trustworthy as the page itself, same as reading the page text would be.
window.addEventListener("message", (event) => {
  if (event.source !== window || event.data?.source !== PAGE_SOURCE) return;
  const meta = event.data.meta;
  if (typeof meta?.id !== "string" || typeof meta.title !== "string") return;

  if (meta.offPage) {
    if (typeof meta.playerId !== "string") return;
    offPage.delete(meta.id);
    offPage.set(meta.id, meta);
    if (offPage.size > 50) offPage.delete(offPage.keys().next().value);
    const known = recentlyBlocked.get(meta.id);
    if (known && known.until > Date.now()) {
      stopOffPage(meta, known.reason, known.hint);
      return;
    }
  } else {
    current = meta;
  }

  // One readable line, with every field the classifier will get tucked
  // underneath it (click the line in DevTools to expand).
  const where = meta.offPage ? ` {${meta.inMiniplayer ? "miniplayer" : meta.playerId}}` : "";
  console.groupCollapsed(TAG, `video ${meta.id}: "${meta.title}" by ${meta.channel ?? "unknown channel"}${meta.isShort ? " (Short)" : ""} [${meta.category ?? "no category"}]${where}`);
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

function toPage(message) {
  window.postMessage({ source: TO_PAGE, ...message }, location.origin);
}

// Which video a verdict is for. "main" is the address video (a verdict
// without an id is treated as that, the old behaviour). An id that is
// neither the address video nor a known off-page one is a stale verdict for
// a video that's gone: ignored.
function verdictTarget(videoId) {
  if (!videoId || videoId === addressVideoId()) return { kind: "main" };
  const meta = offPage.get(videoId);
  return meta ? { kind: "offPage", meta } : { kind: "stale" };
}

// Stops an off-page video in its own player. The miniplayer gets a small
// notice (the viewer chose that video); a hover preview is stopped silently,
// since one notice per hovered thumbnail would be noise.
function stopOffPage(meta, reason, hint) {
  toPage({ cmd: "stop", playerId: meta.playerId, videoId: meta.id });
  if (meta.inMiniplayer) {
    window.SBOverlay?.showNotice(`Stopped: ${reason || "classified as a distraction"}${hint ? ` ${hint}` : ""}`);
  }
  console.info(TAG, `stopped ${meta.id} in ${meta.inMiniplayer ? "the miniplayer" : meta.playerId}:`, reason);
}

function blockOffPage(meta, reason, hint) {
  recentlyBlocked.set(meta.id, { until: Date.now() + BLOCK_MEMORY_MS, reason, hint });
  stopOffPage(meta, reason, hint);
}

// Coming back to this tab (from the desktop app, say) checks the open videos
// again, so a change made there meanwhile applies without a reload:
// switching the extension off lifts a block screen, and switching it back
// on, blocking this channel or turning on education-only mode blocks the
// video. At most every few seconds, since focus also bounces between the
// page and its frames. page-bridge.js re-reports what the players hold right
// now, rather than this file repeating a report that may be stale.
let lastRecheck = 0;
function recheck(force) {
  if (!force && Date.now() - lastRecheck < 3000) return;
  lastRecheck = Date.now();
  recentlyBlocked.clear();
  toPage({ cmd: "recheck" });
}
window.addEventListener("focus", () => recheck(false));
document.addEventListener("visibilitychange", () => {
  if (!document.hidden) recheck(false);
});

// Leaving the video a block screen was shown for (back to search, say) must
// not leave the screen up over an unrelated page. The video stays paused in
// the miniplayer, and playing it again is judged again by page-bridge.js.
document.addEventListener("yt-navigate-finish", () => {
  if (addressVideoId()) return;
  holdPaused = false;
  window.SBOverlay?.hide();
});

// The address video's own <video>: the Shorts player on a Short, the main
// player otherwise (other players, like a hover preview, can come first in
// the page).
function mainVideo() {
  const selector = location.pathname.startsWith("/shorts") ? "#shorts-player video" : "#movie_player video";
  return document.querySelector(selector) ?? document.querySelector("video.html5-main-video, video");
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

// Every playing video, not just the main one: the block screen covers the
// whole page, so nothing else should be heard under it either.
function pause(reason) {
  holdPaused = true;
  let paused = false;
  for (const v of document.querySelectorAll("video")) {
    if (!v.paused) {
      v.pause();
      paused = true;
    }
  }
  if (paused) console.info(TAG, "paused", reason ? `(${reason})` : "");
}

// Plays the video again only if this file was the one holding it paused,
// so a re-check never starts a video you paused yourself.
function resume() {
  const wasHolding = holdPaused;
  holdPaused = false;
  const v = mainVideo();
  if (wasHolding && v && v.paused) {
    v.play().catch(() => {});
    console.info(TAG, "resumed");
  }
}

chrome.runtime.onMessage.addListener((message, _sender, sendResponse) => {
  switch (message?.type) {
    case "resume-video": {
      if (message.videoId) recentlyBlocked.delete(message.videoId);
      // An off-page video was never held, so a Study verdict leaves it be.
      if (verdictTarget(message.videoId).kind === "main") {
        window.SBOverlay?.hide();
        resume();
      }
      sendResponse({ ok: true });
      break;
    }
    case "show-block-screen": {
      const target = verdictTarget(message.videoId);
      if (target.kind === "offPage") {
        blockOffPage(target.meta, message.reason);
      } else if (target.kind === "main") {
        pause(message.reason);
        window.SBOverlay?.showBlockScreen(message.reason, message.logo);
      }
      sendResponse({ ok: true });
      break;
    }
    case "show-grey-popup": {
      const target = verdictTarget(message.videoId);
      // Never a silent allow: an off-page video can't ask the question (the
      // popup is page-wide), so it is stopped until it is opened and answered.
      if (target.kind === "offPage") {
        blockOffPage(target.meta, message.reason, "Open the video to allow or block its channel.");
      } else if (target.kind === "main") {
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
      }
      sendResponse({ ok: true });
      break;
    }
    // The toolbar popup's Block channel button: the video on screen. That's
    // the address video if there is one, otherwise the miniplayer's.
    case "get-current-video": {
      const address = addressVideoId();
      const miniplayer = [...offPage.values()].reverse().find((m) => m.inMiniplayer);
      const video = address ? (current?.id === address ? current : null) : miniplayer ?? null;
      sendResponse(video ? { id: video.id, channel: video.channel ?? null, channelId: video.channelId ?? null } : null);
      break;
    }
    // A channel was just blocked: check the open videos again straight away,
    // so they're blocked without waiting for this tab to be focused.
    case "recheck-video":
      recheck(true);
      sendResponse({ ok: true });
      break;
    default:
      sendResponse({ ok: false, error: "unknown message" });
  }
});
