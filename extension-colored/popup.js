// The toolbar popup. Two faces (the user's request, 2026-11-21):
// - the desktop app is installed: no controls at all, just "Control from
//   App" and whether the app is connected; every setting lives in the app;
// - no app: the extension's own settings (settings.js), the same sections
//   as the app minus blocked apps and education-only mode, with the app's
//   Lock. Outside a lock everything is freely changed or switched off;
//   during one, only made stricter (but switching the extension off in the
//   browser still ends it, which is what the app fixes; the pointer to it is
//   at the bottom).
// The background worker applies a saved change straight away (it listens to
// storage), so this file only reads and writes settings.

// Where the desktop app is downloaded from. Null shows the store's name
// without a link.
const MICROSOFT_STORE_URL = null;
const CHROME_WEB_STORE_URL = null;

const $ = (id) => document.getElementById(id);

function send(type, fields = {}) {
  return new Promise((resolve) => chrome.runtime.sendMessage({ type, ...fields }, resolve));
}

async function main() {
  const status = await send("get-status");
  if (status?.appMissing) await showStandalone();
  else showApp(status);
}

// --- The app is installed ---

function showApp(status) {
  $("app-view").hidden = false;
  const connected = !!status?.connected;
  $("app-state").textContent = connected ? "App connected" : "App not connected";
  $("app-dot").classList.toggle("bad", !connected);
  if (!connected && status?.error) $("app-state").title = status.error;
}

// --- Standalone ---

let settings = { ...SB_DEFAULT_SETTINGS };
const switches = new Map();

// A refused change (the lock is on) just redraws what's really saved.
async function save(change) {
  try {
    settings = await sbUpdateSettings(change);
  } catch (e) {
    console.warn("[Study Blocker]", e.message);
    settings = await sbGetSettings();
  }
  render();
}

const locked = () => sbLockState(settings).locked;

async function showStandalone() {
  settings = await sbGetSettings();
  $("standalone-view").hidden = false;
  $("enabled-switch").hidden = false;
  $("lock-status").hidden = false;
  linkStore($("ms-store"), MICROSOFT_STORE_URL);
  linkStore($("cws"), CHROME_WEB_STORE_URL);

  $("enabled").addEventListener("change", (e) => save(() => ({ enabled: e.target.checked })));
  setUpLock();

  $("site-form").addEventListener("submit", (e) => {
    e.preventDefault();
    const site = sbNormalizeSite($("site-input").value);
    showError("site-error", site ? null : "Enter a site like reddit.com.");
    if (!site) return;
    $("site-input").value = "";
    if (settings.blockedSites.includes(site)) return;
    save((s) => ({ blockedSites: [...s.blockedSites, site] }));
  });

  // Enter blocks rather than allows, as in the app: blocking by mistake is
  // the harmless direction.
  $("channel-form").addEventListener("submit", (e) => {
    e.preventDefault();
    addChannel("Blocked");
  });
  $("allow-channel").addEventListener("click", () => addChannel("Allowed"));

  buildDeclutterList();
  await restoreOpenSections();
  render();
  showVideoOnScreen();

  // A change made elsewhere while this is open (the grey-zone popup on
  // YouTube answered, say).
  chrome.storage.onChanged.addListener((changes, area) => {
    if (area !== "local" || !changes[SB_SETTINGS_KEY]) return;
    settings = { ...SB_DEFAULT_SETTINGS, ...changes[SB_SETTINGS_KEY].newValue };
    render();
    showVideoOnScreen();
  });
  // The time left, and the lock ending while this is open.
  setInterval(render, 15_000);
}

// --- The lock ---

function setUpLock() {
  const slider = $("custom-slider");
  slider.max = String(SB_LOCK_LENGTHS.length - 1);
  // "1 day" under its stop: the thumb's centre runs from half a thumb
  // (about 8px) in from either end.
  const day = SB_LOCK_LENGTHS.indexOf(1440) / (SB_LOCK_LENGTHS.length - 1);
  $("scale-day").style.left = `calc(8px + (100% - 16px) * ${day})`;

  // The slider shows only once Lock (or Extend) is clicked; clicking it
  // again hides it.
  $("lock-button").addEventListener("click", () => {
    $("lock-times").hidden = !$("lock-times").hidden;
    $("lock-button").setAttribute("aria-expanded", String(!$("lock-times").hidden));
    showCustomLength();
    if (!$("lock-times").hidden) slider.focus();
  });
  slider.addEventListener("input", showCustomLength);
  $("custom-go").addEventListener("click", () => lockOrExtend(SB_LOCK_LENGTHS[Number($("custom-slider").value)]));

  $("break-button").addEventListener("click", async () => {
    const ok = await ask("Break the lock early?",
      `It stays fully on for another ${sbDescribeMinutes(SB_BREAK_DELAY_MS / 60_000)}, then ends. You can cancel any time before that.`,
      "Break lock early");
    if (ok) await run(sbBreakLock);
  });
  $("cancel-break").addEventListener("click", () => run(sbCancelLockBreak));
}

// During a lock the time left can't go over 3 days, so the slider stops at
// the longest extension that fits (as the app does with its 7).
function showCustomLength() {
  const state = sbLockState(settings);
  const leftMinutes = state.locked ? Math.ceil(state.lockLeftMs / 60_000) : 0;
  const longest = Math.max(0, SB_LOCK_LENGTHS.findLastIndex((m) => m <= SB_MAX_LOCK_MS / 60_000 - leftMinutes));
  const slider = $("custom-slider");
  if (Number(slider.value) > longest) slider.value = String(longest);
  $("custom-length").textContent = sbDescribeMinutes(SB_LOCK_LENGTHS[Number(slider.value)]);
}

async function lockOrExtend(minutes) {
  const extending = locked();
  const ok = extending
    ? await ask(`Add ${sbDescribeMinutes(minutes)} to the lock?`, "This can't be undone.", "Extend")
    : await ask(`Lock for ${sbDescribeMinutes(minutes)}?`,
      "Until it ends, you can only extend the lock or make it stricter."
        + (settings.enabled ? "" : " This also switches the extension back on."),
      "Lock");
  if (!ok) return;
  $("lock-times").hidden = true;
  $("lock-button").setAttribute("aria-expanded", "false");
  await run(() => (extending ? sbExtendLock(minutes) : sbStartLock(minutes)));
}

async function run(action) {
  try {
    settings = await action();
  } catch (e) {
    console.warn("[Study Blocker]", e.message);
    settings = await sbGetSettings();
  }
  render();
}

// The popup's own yes/no box: it stays inside the popup, which a browser
// dialog wouldn't reliably do.
function ask(question, detail, yesLabel) {
  return new Promise((resolve) => {
    $("confirm-text").textContent = question;
    $("confirm-detail").textContent = detail;
    $("confirm-yes").textContent = yesLabel;
    $("confirm").hidden = false;
    $("confirm-no").focus();
    const done = (answer) => {
      $("confirm").hidden = true;
      $("confirm-yes").onclick = $("confirm-no").onclick = null;
      document.removeEventListener("keydown", onKey);
      resolve(answer);
    };
    const onKey = (e) => {
      if (e.key === "Escape") done(false);
    };
    document.addEventListener("keydown", onKey);
    $("confirm-yes").onclick = () => done(true);
    $("confirm-no").onclick = () => done(false);
  });
}

function linkStore(span, url) {
  if (!url) return;
  const link = document.createElement("a");
  link.href = url;
  link.target = "_blank";
  link.rel = "noopener noreferrer";
  link.textContent = span.textContent;
  span.replaceWith(link);
}

function showError(id, text) {
  $(id).textContent = text ?? "";
  $(id).hidden = !text;
}

function addChannel(decision) {
  const name = $("channel-input").value.trim();
  showError("channel-error", name ? null : "Type a channel's name first, as it appears under its videos.");
  if (!name) return;
  $("channel-input").value = "";
  save((s) => ({ channels: sbSetChannel(s.channels, { id: null, name }, decision) }));
}

// The two collapsible sections stay as they were left last time.
async function restoreOpenSections() {
  const { popup } = await chrome.storage.local.get("popup");
  for (const id of ["sites-card", "youtube-card"]) {
    $(id).open = !!popup?.[id];
    $(id).addEventListener("toggle", async () => {
      const { popup: now } = await chrome.storage.local.get("popup");
      await chrome.storage.local.set({ popup: { ...now, [id]: $(id).open } });
    });
  }
}

// A switch with sub-options gets a "+" at the end of its row; the
// sub-options stay folded away until it's clicked (then "−" folds them
// again). Folded every time the popup opens (the user's request,
// 2026-11-21).
function buildDeclutterList() {
  const list = $("declutter-list");
  for (const item of SB_DECLUTTER_ITEMS.filter((i) => !i.parent)) {
    const row = document.createElement("div");
    row.className = "toggle-row";
    row.append(declutterSwitch(item));
    list.append(row);

    const children = SB_DECLUTTER_ITEMS.filter((i) => i.parent === item.option);
    if (!children.length) continue;
    const rail = document.createElement("div");
    rail.className = "rail";
    rail.id = `sub-${item.option}`;
    rail.hidden = true;
    rail.append(...children.map(declutterSwitch));
    list.append(rail);

    const expand = document.createElement("button");
    expand.className = "expand";
    expand.type = "button";
    expand.setAttribute("aria-controls", rail.id);
    const show = (open) => {
      rail.hidden = !open;
      expand.textContent = open ? "−" : "+";
      expand.setAttribute("aria-expanded", String(open));
      expand.title = `${open ? "Hide" : "Show"} the options under ${item.label.toLowerCase()}`;
      expand.setAttribute("aria-label", expand.title);
    };
    show(false);
    expand.addEventListener("click", () => show(rail.hidden));
    row.append(expand);
  }
}

function declutterSwitch(item) {
  const label = document.createElement("label");
  label.className = "switch";
  const input = document.createElement("input");
  input.type = "checkbox";
  input.addEventListener("change", async () => {
    const on = input.checked;
    // During a lock a switch turned on stays on, so ask first, as the app does.
    if (on && locked() && !(await ask(`${item.label}?`, "You can't turn it off again until the lock ends.", "Turn on"))) {
      input.checked = false;
      return;
    }
    save((s) => ({ declutter: on ? [...new Set([...s.declutter, item.option])] : s.declutter.filter((o) => o !== item.option) }));
  });
  const track = document.createElement("span");
  track.className = "track";
  const text = document.createElement("span");
  text.textContent = item.label;
  label.append(input, track, text);
  switches.set(item.option, input);
  return label;
}

function render() {
  const lock = sbLockState(settings);
  const held = lock.locked;

  // The lock, as the app shows it.
  $("lock-status").textContent = held ? `Locked · ${sbRemaining(lock.leftMs)} left` : "Not locked";
  $("lock-button").textContent = held ? "Extend" : "Lock";
  $("lock-label").textContent = held ? "Extend the lock by" : "Lock for";
  $("custom-go").textContent = held ? "Extend" : "Lock";
  $("break-banner").hidden = !lock.breaking;
  $("break-text").textContent = lock.breaking ? `Breaking the lock early: it ends in ${sbRemaining(lock.leftMs)} unless you cancel.` : "";
  $("lock-row").hidden = lock.breaking;
  if (lock.breaking) $("lock-times").hidden = true;
  $("break-button").hidden = !held || lock.breaking || lock.lockLeftMs <= SB_BREAK_DELAY_MS;
  $("locked-note").hidden = !held;
  showCustomLength();

  // Switched off only outside a lock; a lock always has it on.
  $("enabled").checked = settings.enabled;
  $("enabled").disabled = held && settings.enabled;
  $("enabled-label").textContent = settings.enabled ? "On" : "Off";
  $("off-note").hidden = settings.enabled;

  // Blocked sites: no remove buttons during a lock.
  const sites = settings.blockedSites;
  $("sites-summary").textContent = sites.length ? `Show blocked sites (${sites.length})` : "Show blocked sites";
  $("sites-list").replaceChildren(...(sites.length
    ? sites.map((site) => row(site, null, `Remove ${site}`,
        held ? null : () => save((s) => ({ blockedSites: s.blockedSites.filter((x) => x !== site) }))))
    : [empty("Nothing blocked yet.")]));

  // Channels: allowed first, then blocked, each by name, as in the app.
  // During a lock Allow goes, and only an allowed channel can be taken off
  // (that's stricter); a blocked one stays.
  $("allow-channel").hidden = held;
  const channels = [...settings.channels].sort((a, b) =>
    (a.decision === "Blocked") - (b.decision === "Blocked") || a.name.localeCompare(b.name, undefined, { sensitivity: "base" }));
  $("channels-list").replaceChildren(...(channels.length
    ? channels.map((c) => row(c.name || c.id, c.decision === "Allowed" ? "Always allowed" : "Blocked", `Remove ${c.name || c.id}`,
        held && c.decision === "Blocked" ? null : () => save((s) => ({ channels: sbSetChannel(s.channels, { id: c.id, name: c.name }, null) }))))
    : [empty("No channels yet. Your answers to the popup on YouTube show up here too.")]));

  // A covered sub-option shows as on while its parent hides the whole area.
  // During a lock a switch that's on can't be turned off.
  for (const item of SB_DECLUTTER_ITEMS) {
    const input = switches.get(item.option);
    const ticked = settings.declutter.includes(item.option);
    const covered = !!item.covered && settings.declutter.includes(item.parent);
    input.checked = covered || ticked;
    input.disabled = covered || (held && ticked);
  }
}

// onRemove null: no remove button (the lock holds it).
function row(text, tag, removeLabel, onRemove) {
  const li = document.createElement("li");
  li.className = "row";
  const span = document.createElement("span");
  span.className = "row-text";
  span.textContent = span.title = text;
  li.append(span);
  if (tag) {
    const t = document.createElement("span");
    t.className = "tag";
    t.textContent = tag;
    li.append(t);
  }
  if (!onRemove) return li;
  const remove = document.createElement("button");
  remove.className = "remove";
  remove.type = "button";
  remove.textContent = "×";
  remove.title = removeLabel;
  remove.setAttribute("aria-label", removeLabel);
  remove.addEventListener("click", onRemove);
  li.append(remove);
  return li;
}

function empty(text) {
  const li = document.createElement("li");
  li.className = "empty";
  li.textContent = text;
  return li;
}

// --- Block the channel of the video on screen ---

function isVideoPage(url) {
  try {
    const { hostname, pathname } = new URL(url);
    return (hostname === "youtube.com" || hostname.endsWith(".youtube.com"))
      && (pathname === "/watch" || pathname.startsWith("/shorts/"));
  } catch {
    return false;
  }
}

// The video in this window's open tab, as its content script last saw it.
async function videoOnScreen() {
  const [tab] = await chrome.tabs.query({ active: true, currentWindow: true });
  if (!tab?.id || !isVideoPage(tab.url)) return null;
  const video = await chrome.tabs.sendMessage(tab.id, { type: "get-current-video" }).catch(() => null);
  return video?.channelId || video?.channel ? video : null;
}

async function showVideoOnScreen() {
  const video = await videoOnScreen();
  $("on-screen").hidden = !video;
  if (!video) return;
  $("on-screen-name").textContent = $("on-screen-name").title = video.channel ?? "This video's channel";
  const rule = sbFindChannel(settings.channels, video.channelId, video.channel);
  const button = $("on-screen-block");
  button.hidden = rule?.decision === "Blocked";
  $("on-screen-state").hidden = !rule;
  $("on-screen-state").textContent = rule?.decision === "Blocked" ? "Blocked" : "Always allowed";
  button.onclick = () => save((s) => ({
    channels: sbSetChannel(s.channels, { id: video.channelId ?? null, name: video.channel }, "Blocked"),
  })).then(showVideoOnScreen);
}

main();
