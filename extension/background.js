// The only part of the extension that talks to the service. It reaches the
// SYSTEM service through a native-messaging host (a small bridge process the
// browser starts as the current user), which relays each request to the
// service's named pipe and hands back the reply. One request per call, to
// match how the service answers one request per pipe connection.

const TAG = "[Study Blocker]";
const HOST = "com.smartstudyblocker.host";
const POLL_ALARM = "poll-service";

// Last thing the service told us, kept so the popup can show it without
// waiting on a round trip.
let lastStatus = { connected: false, error: "not checked yet" };

function callService(request) {
  return new Promise((resolve) => {
    let settled = false;
    const done = (value) => {
      if (!settled) {
        settled = true;
        resolve(value);
      }
    };
    try {
      chrome.runtime.sendNativeMessage(HOST, request, (reply) => {
        if (chrome.runtime.lastError) {
          done({ ok: false, error: chrome.runtime.lastError.message });
        } else if (!reply) {
          done({ ok: false, error: "empty reply from host" });
        } else {
          // The host relays the service's reply verbatim (PascalCase);
          // normalise it to the lowercase shape the rest of this file uses.
          done({ ok: !!reply.Ok, payload: reply.Payload, error: reply.Error });
        }
      });
    } catch (e) {
      done({ ok: false, error: String(e) });
    }
    // The host bridge should reply in well under a second; guard against a
    // wedged host so the popup never hangs.
    setTimeout(() => done({ ok: false, error: "timed out waiting for host" }), 4000);
  });
}

async function refreshStatus() {
  const reply = await callService({ Type: "get-status" });
  if (reply.ok && reply.payload) {
    lastStatus = { connected: true, status: reply.payload };
    await rememberLocked(reply);
  } else {
    lastStatus = { connected: false, error: reply.error ?? "unknown error" };
  }
  console.info(TAG, "service status", JSON.stringify(lastStatus));
  return lastStatus;
}

// The last lock state the service reported, for the fail-safe in
// handleVideoMetadata: when the service can't be reached at all, there's no
// asking it whether a lock is on. Kept in session storage because MV3 service
// workers are shut down when idle and lose their variables.
async function rememberLocked(statusReply) {
  if (!statusReply.ok || !statusReply.payload) return;
  await chrome.storage.session.set({ locked: !!statusReply.payload.Lock?.IsLocked });
}

async function lastKnownLocked() {
  return !!(await chrome.storage.session.get("locked")).locked;
}

// Milestone 6: the ticked options come from the RuleSet (get-status), and are
// resolved to selectors here rather than in declutter.js: a content script's
// fetch of an extension file counts as the web page's own request, which
// Chrome blocks (rewriting it to chrome-extension://invalid/) unless the file
// is declared web-accessible — and declaring it would let any site detect
// this extension. The service worker runs on the extension's own origin.
// Not ok when the service couldn't be asked, so the page keeps whatever it
// already hides rather than taking it all away. Switched off in the app,
// nothing is hidden.
async function getDeclutter() {
  const [reply, selectorMap] = await Promise.all([
    callService({ Type: "get-status" }),
    fetch(chrome.runtime.getURL("data/declutter-selectors.json")).then((r) => r.json()),
  ]);
  if (!reply.ok) return { ok: false, options: [], selectors: [] };
  const rules = reply.payload?.Lock?.Rules;
  const options = rules?.ExtensionEnabled === false ? [] : (rules?.Declutter ?? []);
  return { ok: true, options, selectors: options.flatMap((option) => selectorMap[option] ?? []) };
}

function resumeTab(tabId) {
  if (tabId == null) return;
  chrome.tabs.sendMessage(tabId, { type: "resume-video" }).catch(() => {});
}

// The logo for the in-page block screen, as a data URL: a content script
// can't load the extension's own files unless they're declared
// web-accessible, which would let any site detect the extension (the same
// reason as getDeclutter above).
let logoDataUrl = null;
async function logo() {
  if (logoDataUrl === null) {
    const bytes = new Uint8Array(await (await fetch(chrome.runtime.getURL("images/logo-mark.png"))).arrayBuffer());
    let binary = "";
    for (const byte of bytes) binary += String.fromCharCode(byte);
    logoDataUrl = `data:image/png;base64,${btoa(binary)}`;
  }
  return logoDataUrl;
}

async function showBlockScreenTab(tabId, reason) {
  if (tabId == null) return;
  const image = await logo().catch(() => null);
  chrome.tabs.sendMessage(tabId, { type: "show-block-screen", reason, logo: image }).catch(() => {});
}

// The popup's answer comes back with the channel's name (shown in the
// desktop app's lists) and the video id, which the service checks during a
// lock: only a video it judged Grey itself can get its channel allowed.
function showGreyPopupTab(tabId, reason, meta) {
  if (tabId == null) return;
  chrome.tabs.sendMessage(tabId, {
    type: "show-grey-popup",
    reason,
    channelId: meta.channelId ?? null,
    channelName: meta.channel ?? null,
    videoId: meta.id,
  }).catch(() => {});
}

// The metadata shape page-bridge.js reports (camelCase, matching the page's
// own JS) to the shape the service's classify-video request expects
// (PascalCase, matching StudyBlocker.Common.VideoMetadata).
function toClassifyPayload(meta) {
  return {
    Id: meta.id,
    Title: meta.title ?? "",
    Channel: meta.channel ?? null,
    ChannelId: meta.channelId ?? null,
    Category: meta.category ?? null,
    Description: meta.description ?? "",
    Keywords: meta.keywords ?? [],
    LengthSeconds: meta.lengthSeconds ?? null,
    IsLive: !!meta.isLive,
    IsShort: !!meta.isShort,
  };
}

// Every verdict is enforced, locked or not (plan §10: outside a lock "the
// same rules still apply ... but everything is freely editable"). A lock
// only stops the rules being loosened (the service's ratchet), plus the
// fail-safe below. Up to Milestone 7 this was lock-only, so outside a lock a
// distraction was just logged, which isn't what the plan or the user meant.
// With the extension switched off in the app, the service answers every
// video with Study [switched-off], which lifts anything showing.
async function handleVideoMetadata(meta, tabId) {
  const [statusReply, classifyReply] = await Promise.all([
    callService({ Type: "get-status" }),
    callService({ Type: "classify-video", Payload: toClassifyPayload(meta) }),
  ]);
  await rememberLocked(statusReply);

  if (!classifyReply.ok || !classifyReply.payload) {
    console.warn(TAG, "classification failed for", meta.id, ":", classifyReply.error ?? "no reply");
    // Fail-safe (plan §17): a classifier failure during a lock must never
    // behave like a silent allow. Outside a lock there's nothing to go on,
    // so the video plays.
    if (await lastKnownLocked()) showBlockScreenTab(tabId, "couldn't reach the classifier while locked");
    return;
  }

  const { Verdict: verdict, Reason: reason, Source: source } = classifyReply.payload;
  console.info(TAG, `verdict for ${meta.id} (${meta.title}): ${verdict} [${source}] — ${reason}`);

  if (verdict === "Grey") showGreyPopupTab(tabId, reason, meta);
  else if (verdict === "Study") resumeTab(tabId);
  else showBlockScreenTab(tabId, reason);
}

// The grey-zone popup's Allow/Block buttons land here (never straight from
// the content script — it has no service logic of its own).
async function answerGreyPopup({ channelId, channelName, videoId, decision }, tabId) {
  const setReply = await callService({
    Type: "set-channel-rule",
    Payload: {
      ChannelId: channelId,
      Name: channelName ?? null,
      Rule: decision === "allow" ? "allow" : "block",
      VideoId: videoId ?? null,
    },
  });
  if (!setReply.ok) {
    console.warn(TAG, "couldn't save the channel rule for", channelId, ":", setReply.error);
    // Fail-safe (plan §17): a Grey verdict must never resolve into silent
    // playback, even when saving the decision itself failed (e.g. the
    // during-lock ratchet refused it because another tab already blocked
    // this channel this lock).
    showBlockScreenTab(tabId, setReply.error ?? "couldn't save your answer");
    return;
  }

  if (decision === "allow") resumeTab(tabId);
  else showBlockScreenTab(tabId, "you blocked this channel");
}

// The toolbar popup's Block channel button, the extension's one control
// (the user's request, 2026-09-30). Blocking only ever makes things
// stricter, so the service accepts it at any time, lock or not, unlike the
// old Resume button, which could lift a block screen during a lock.
async function blockChannel({ channelId, channelName, videoId }) {
  const reply = await callService({
    Type: "set-channel-rule",
    Payload: { ChannelId: channelId ?? null, Name: channelName ?? null, Rule: "block", VideoId: videoId ?? null },
  });
  if (!reply.ok) return { ok: false, error: reply.error ?? "couldn't reach the app" };
  // Every open YouTube tab checks its video again, so this one, and any
  // other showing a video from this channel, is blocked straight away.
  const tabs = await chrome.tabs.query({ url: "*://*.youtube.com/*" });
  for (const tab of tabs) chrome.tabs.sendMessage(tab.id, { type: "recheck-video" }).catch(() => {});
  return { ok: true };
}

// Which list a channel is on, if any, for the popup: an entry saved with the
// channel's id first, otherwise one typed into the app by its name, the same
// order the service checks them in.
async function channelDecision({ channelId, channelName }) {
  const reply = await callService({ Type: "list-channel-rules" });
  if (!reply.ok) return { ok: false, error: reply.error ?? "couldn't reach the app" };
  const rules = reply.payload ?? [];
  const sameName = (a, b) => !!a?.trim() && !!b?.trim() && a.trim().toLowerCase() === b.trim().toLowerCase();
  const rule = (channelId && rules.find((r) => r.ChannelId === channelId))
    || rules.find((r) => !r.ChannelId && sameName(r.Name, channelName));
  return { ok: true, decision: rule?.Decision ?? null };
}

// Every message here is a status read or part of enforcement, apart from
// Block channel above. There are deliberately no other controls in the
// extension (no pause/resume, no settings): those live in the desktop app.
chrome.runtime.onMessage.addListener((message, sender, sendResponse) => {
  switch (message?.type) {
    case "get-status":
      refreshStatus().then(sendResponse);
      return true;
    case "video-metadata":
      handleVideoMetadata(message.meta, sender.tab?.id).catch((e) =>
        console.warn(TAG, "verdict handling failed", e));
      sendResponse({ ok: true });
      return false;
    case "answer-grey-popup":
      answerGreyPopup(message, sender.tab?.id).catch((e) =>
        console.warn(TAG, "answering the grey-zone popup failed", e));
      sendResponse({ ok: true });
      return false;
    case "get-declutter":
      getDeclutter().then(sendResponse);
      return true;
    case "channel-decision":
      channelDecision(message).then(sendResponse);
      return true;
    case "block-channel":
      blockChannel(message).then(sendResponse);
      return true;
    default:
      sendResponse({ ok: false, error: "unknown message" });
      return false;
  }
});

// Sites on the blocked list are blocked by browser policy, and the browser
// then shows its own "This page is blocked" page, which can't be changed. So
// once the browser has blocked an address, the tab is sent to the
// extension's own page instead: blocked.html, with the logo and "Blocked by
// Smart Study Distraction Blocker". The policy still does the blocking,
// whether or not the extension is running (plan §17); this only changes
// what the page looks like. Switched off in the app, the service lifts the
// policy, so nothing gets here.
const BLOCKED_PAGE = chrome.runtime.getURL("blocked.html");

// Where each tab was when its latest navigation started (in MV3, tab.url is
// the last page a tab actually showed), so that going Back from
// blocked.html, which lands on the blocked address again, skips past it
// instead of showing blocked.html once more.
const navigationStartedFrom = new Map();

chrome.webNavigation.onBeforeNavigate.addListener((details) => {
  if (details.frameId !== 0) return;
  navigationStartedFrom.set(details.tabId,
    chrome.tabs.get(details.tabId).then((tab) => tab.url ?? "", () => ""));
});

chrome.webNavigation.onErrorOccurred.addListener(async (details) => {
  if (details.frameId !== 0 || details.documentLifecycle === "prerender") return;
  if (!details.error.includes("ERR_BLOCKED_BY_ADMINISTRATOR")) return;
  const from = await (navigationStartedFrom.get(details.tabId) ?? "");
  navigationStartedFrom.delete(details.tabId);

  const site = new URL(details.url).hostname;
  const page = `${BLOCKED_PAGE}?site=${encodeURIComponent(site)}`;
  if (from.startsWith(BLOCKED_PAGE) && new URL(from).searchParams.get("site") === site) {
    // Back from blocked.html (or the same site typed again from it). With
    // nothing further back, blocked.html shows again.
    chrome.tabs.goBack(details.tabId).catch(() => chrome.tabs.update(details.tabId, { url: page }).catch(() => {}));
    return;
  }
  chrome.tabs.update(details.tabId, { url: page }).catch(() => {});
});

// A light heartbeat so the service connection is checked even with no popup
// open. MV3 workers sleep between events; the alarm wakes them. No top-level
// call: the worker re-runs this file on every wake, which would double up
// with the alarm.
function startHeartbeat() {
  chrome.alarms.create(POLL_ALARM, { periodInMinutes: 1 });
  refreshStatus();
}
chrome.runtime.onInstalled.addListener(startHeartbeat);
chrome.runtime.onStartup.addListener(startHeartbeat);
chrome.alarms.onAlarm.addListener((alarm) => {
  if (alarm.name === POLL_ALARM) refreshStatus();
});
