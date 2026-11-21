// The only part of the extension that talks to the service. It reaches the
// SYSTEM service through a native-messaging host (a small bridge process the
// browser starts as the current user), which relays each request to the
// service's named pipe and hands back the reply. One request per call, to
// match how the service answers one request per pipe connection.
//
// Without the desktop app (no native-messaging host registered, which is
// what a store install on a PC without the app gets) the extension runs in
// standalone mode (2026-11-21): its own settings from the popup
// (settings.js), its own copy of the classifier (classifier.js), and blocked
// sites through declarativeNetRequest instead of browser policy. Every
// handler below asks the service first and falls back to standalone only on
// that "no host" answer; an installed app whose service is down keeps the
// app's behaviour (and its fail-safe).

importScripts("settings.js", "classifier.js");

const TAG = "[Study Blocker]";
// colored copy: a host name that is never registered, so this copy always runs standalone.
const HOST = "com.smartstudyblocker.colored_copy_standalone";
const POLL_ALARM = "poll-service";

// The browser's own wording when no native-messaging host is registered under
// HOST: the desktop app isn't installed, so standalone mode applies.
const HOST_NOT_REGISTERED = /native messaging host not found/i;

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
          const error = chrome.runtime.lastError.message;
          done({ ok: false, error, appMissing: HOST_NOT_REGISTERED.test(error) });
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
    lastStatus = { connected: false, error: reply.error ?? "unknown error", appMissing: !!reply.appMissing };
    if (reply.appMissing) await chrome.storage.session.set({ locked: false });
  }
  console.info(TAG, "service status", JSON.stringify(lastStatus));
  await syncSiteRules(!!lastStatus.appMissing);
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
  let options;
  if (reply.appMissing) {
    const settings = await sbGetSettings();
    options = settings.enabled ? settings.declutter : [];
  } else if (!reply.ok) {
    return { ok: false, options: [], selectors: [], css: [] };
  } else {
    const rules = reply.payload?.Lock?.Rules;
    options = rules?.ExtensionEnabled === false ? [] : (rules?.Declutter ?? []);
  }
  // An option this copy doesn't know (a newer service) has no entry, so it
  // simply hides nothing here.
  return {
    ok: true,
    options,
    selectors: [...new Set(options.flatMap((option) => selectorMap[option] ?? []))],
    css: options.flatMap((option) => selectorMap["@css"]?.[option] ?? []),
  };
}

// Every verdict names its video: a tab can be playing more than one (the
// address video plus the miniplayer or a hover preview, see youtube.js), and
// each is handled in its own player.
function resumeTab(tabId, videoId) {
  if (tabId == null) return;
  chrome.tabs.sendMessage(tabId, { type: "resume-video", videoId: videoId ?? null }).catch(() => {});
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

async function showBlockScreenTab(tabId, reason, videoId) {
  if (tabId == null) return;
  const image = await logo().catch(() => null);
  chrome.tabs.sendMessage(tabId, { type: "show-block-screen", reason, logo: image, videoId: videoId ?? null }).catch(() => {});
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
  if (classifyReply.appMissing) {
    applyVerdict(await sbClassify(meta, await sbGetSettings()), meta, tabId);
    return;
  }
  await rememberLocked(statusReply);

  if (!classifyReply.ok || !classifyReply.payload) {
    console.warn(TAG, "classification failed for", meta.id, ":", classifyReply.error ?? "no reply");
    // Fail-safe (plan §17): a classifier failure during a lock must never
    // behave like a silent allow. Outside a lock there's nothing to go on,
    // so the video plays.
    if (await lastKnownLocked()) showBlockScreenTab(tabId, "couldn't reach the classifier while locked", meta.id);
    return;
  }

  const { Verdict: verdict, Reason: reason, Source: source } = classifyReply.payload;
  applyVerdict({ verdict, reason, source }, meta, tabId);
}

function applyVerdict({ verdict, reason, source }, meta, tabId) {
  const where = meta.offPage ? ` {${meta.inMiniplayer ? "miniplayer" : meta.playerId}}` : "";
  console.info(TAG, `verdict for ${meta.id} (${meta.title})${where}: ${verdict} [${source}] — ${reason}`);

  if (verdict === "Grey") showGreyPopupTab(tabId, reason, meta);
  else if (verdict === "Study") resumeTab(tabId, meta.id);
  else showBlockScreenTab(tabId, reason, meta.id);
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
  if (setReply.appMissing) {
    // Standalone: saved straight into the extension's own channel list. The
    // settings change makes every YouTube tab check its videos again.
    try {
      await sbUpdateSettings((s) => ({
        channels: sbSetChannel(s.channels, { id: channelId ?? null, name: channelName }, decision === "allow" ? "Allowed" : "Blocked"),
      }));
    } catch (e) {
      // Refused by the lock: never a silent allow.
      showBlockScreenTab(tabId, e.message, videoId);
      return;
    }
    if (decision === "allow") resumeTab(tabId, videoId);
    else showBlockScreenTab(tabId, "you blocked this channel", videoId);
    return;
  }
  if (!setReply.ok) {
    console.warn(TAG, "couldn't save the channel rule for", channelId, ":", setReply.error);
    // Fail-safe (plan §17): a Grey verdict must never resolve into silent
    // playback, even when saving the decision itself failed (e.g. the
    // during-lock ratchet refused it because another tab already blocked
    // this channel this lock).
    showBlockScreenTab(tabId, setReply.error ?? "couldn't save your answer", videoId);
    return;
  }

  if (decision === "allow") resumeTab(tabId, videoId);
  else showBlockScreenTab(tabId, "you blocked this channel", videoId);
}

// --- Standalone mode: blocked sites and settings changes ---

// Blocked sites without the app: one declarativeNetRequest "block" rule per
// site (main page and frames). Blocking needs no host permission, unlike a
// redirect, so the browser shows its own error for a moment and the
// onErrorOccurred listener below swaps in blocked.html, as it does for the
// app's policy blocks. With the app installed, or switched off, there are no
// rules: the app's browser policy does the blocking.
let siteRulesShown = null;
async function syncSiteRules(standalone) {
  const settings = standalone ? await sbGetSettings() : null;
  const sites = settings?.enabled ? settings.blockedSites : [];
  const key = JSON.stringify(sites);
  if (key === siteRulesShown) return;
  const existing = await chrome.declarativeNetRequest.getDynamicRules();
  const addRules = sites.map((site, i) => {
    const [host, ...rest] = site.split("/");
    const path = rest.join("/");
    return {
      id: i + 1,
      priority: 1,
      action: { type: "block" },
      condition: {
        ...(path ? { urlFilter: `||${host}/${path}` } : { requestDomains: [host] }),
        resourceTypes: ["main_frame", "sub_frame"],
      },
    };
  });
  try {
    await chrome.declarativeNetRequest.updateDynamicRules({ removeRuleIds: existing.map((r) => r.id), addRules });
    siteRulesShown = key;
    console.info(TAG, `standalone blocked sites: ${sites.length ? sites.join(", ") : "none"}`);
  } catch (e) {
    console.warn(TAG, "couldn't update the blocked-site rules", e);
  }
}

// The popup saved a standalone setting (or the grey-zone popup did): apply
// it now rather than at the next page load. Sites through syncSiteRules; open
// YouTube tabs check their videos again, so a newly blocked channel
// applies at once and a removed block lifts.
// declutter.js listens for the same change itself.
chrome.storage.onChanged.addListener((changes, area) => {
  if (area !== "local" || !changes[SB_SETTINGS_KEY]) return;
  refreshStatus().then(async () => {
    if (!lastStatus.appMissing) return;
    const tabs = await chrome.tabs.query({ url: "*://*.youtube.com/*" });
    for (const tab of tabs) chrome.tabs.sendMessage(tab.id, { type: "recheck-video" }).catch(() => {});
  });
});

// Every message here is a status read or part of enforcement. With the app
// installed the extension has no controls at all (its popup says "Control
// from App", the user's request, 2026-11-21); in standalone mode the popup
// edits its settings in storage directly, and the listener above applies
// them.
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
// policy, so nothing gets here. In standalone mode the block is the
// extension's own (syncSiteRules), which the browser reports as
// ERR_BLOCKED_BY_CLIENT; that error also comes from other extensions (ad
// blockers), so it only counts when one of our own blocked sites covers the
// address.
const BLOCKED_PAGE = chrome.runtime.getURL("blocked.html");

async function blockedByUs(details) {
  if (details.error.includes("ERR_BLOCKED_BY_ADMINISTRATOR")) return true;
  if (!details.error.includes("ERR_BLOCKED_BY_CLIENT")) return false;
  // Rules exist only while standalone mode is on (see syncSiteRules).
  if (!(await chrome.declarativeNetRequest.getDynamicRules()).length) return false;
  const settings = await sbGetSettings();
  return settings.enabled && settings.blockedSites.some((site) => sbSiteCovers(site, details.url));
}

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
  if (!(await blockedByUs(details))) return;
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
