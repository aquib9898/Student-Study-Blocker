// Standalone mode (the user's request, 2026-11-21): the extension works on
// its own when the desktop app isn't installed, i.e. when the browser has no
// native-messaging host registered for it. Its settings are then its own,
// kept in chrome.storage.local and edited in the toolbar popup: blocked
// sites, YouTube channels and the "Hide on YouTube" list, plus an on/off
// switch. Education-only mode is the app's alone (removed from the
// extension at the user's request, 2026-11-21). Once the app is installed,
// its own rules apply and these are left untouched (and unused).
//
// The lock (added the same day, at the user's request): the app's Lock and
// its rules, set with a slider only (no presets), up to 3 days rather than
// the app's 7. While it runs, settings can only get stricter,
// the on/off switch stays on, and "Break lock early" ends it only after a
// 2-hour countdown. Unlike the app's, nothing outside the browser enforces
// it: switching the extension off in the browser's extensions page ends it
// in practice (the user accepted that; the app is what makes a lock
// unbreakable), and it runs on the PC's own clock.
//
// Shared by the popup (a <script> tag) and the background worker
// (importScripts), so everything here is a plain global prefixed "sb".

const SB_SETTINGS_KEY = "standalone";

const SB_DEFAULT_SETTINGS = Object.freeze({
  enabled: true,
  blockedSites: [],
  // { id: channel id or null (typed by name), name, decision: "Blocked" | "Allowed" }
  channels: [],
  // DeclutterOption names, the same as the app's (data/declutter-selectors.json keys).
  declutter: [],
  // null, or { endsAt, breakEndsAt } in ms since the epoch (breakEndsAt null
  // unless the lock is being broken early).
  lock: null,
});

async function sbGetSettings() {
  const stored = (await chrome.storage.local.get(SB_SETTINGS_KEY))[SB_SETTINGS_KEY] ?? {};
  return { ...SB_DEFAULT_SETTINGS, ...stored };
}

// change(settings) returns the fields to replace. During a lock a change
// that would loosen anything is refused (throws), whoever asks: the popup
// already hides those controls, so this only stops a stale popup or a bug.
async function sbUpdateSettings(change) {
  const settings = await sbGetSettings();
  const next = { ...settings, ...change(settings) };
  if (sbLockState(settings).locked && !sbAtLeastAsStrict(next, settings)) {
    throw new Error("The lock is on: things can only be made stricter until it ends.");
  }
  await chrome.storage.local.set({ [SB_SETTINGS_KEY]: next });
  return next;
}

// --- The lock ---

const SB_HOUR_MS = 60 * 60 * 1000;
// 3 days in the browser (the app allows 7; the user's choice, 2026-11-21).
const SB_MAX_LOCK_MS = 3 * 24 * SB_HOUR_MS;
const SB_BREAK_DELAY_MS = 2 * SB_HOUR_MS;

// The lock slider's stops, in minutes, spaced like the app's Custom slider:
// every hour up to a day, then every 6 hours up to 3 days. No preset
// buttons (the user's choice).
const SB_LOCK_LENGTHS = Object.freeze([
  ...Array.from({ length: 24 }, (_, i) => (i + 1) * 60),
  ...Array.from({ length: 8 }, (_, i) => (24 + (i + 1) * 6) * 60),
]);

// { locked, breaking, leftMs }: leftMs counts to whichever ends first, the
// lock or its break-early countdown.
function sbLockState(settings, now = Date.now()) {
  const lock = settings.lock;
  if (!lock?.endsAt) return { locked: false, breaking: false, leftMs: 0 };
  const endsAt = lock.breakEndsAt ? Math.min(lock.endsAt, lock.breakEndsAt) : lock.endsAt;
  if (now >= endsAt) return { locked: false, breaking: false, leftMs: 0 };
  return { locked: true, breaking: !!lock.breakEndsAt, leftMs: endsAt - now, lockLeftMs: lock.endsAt - now };
}

// The app's RuleSet.IsAtLeastAsStrictAs, for standalone settings. A newly
// allowed channel is let through: that only comes from the grey-zone popup
// on YouTube, which the app also answers during a lock. The lock itself may
// only last longer, or start or stop being broken early (sbBreakLock /
// sbCancelLockBreak).
function sbAtLeastAsStrict(next, prev) {
  if (prev.enabled && !next.enabled) return false;
  if (prev.blockedSites.some((s) => !next.blockedSites.includes(s))) return false;
  if (prev.declutter.some((o) => !next.declutter.includes(o))) return false;
  const stillBlocked = (c) => next.channels.some((n) => n.decision === "Blocked"
    && (c.id ? n.id === c.id : !n.id && sbSameName(n.name, c.name)));
  if (prev.channels.some((c) => c.decision === "Blocked" && !stillBlocked(c))) return false;
  if (!next.lock || next.lock.endsAt < prev.lock.endsAt) return false;
  return true;
}

// Starting a lock also switches the extension back on, as in the app.
function sbStartLock(minutes) {
  return sbUpdateSettings((s) => {
    if (sbLockState(s).locked) throw new Error("Already locked.");
    return { enabled: true, lock: { endsAt: Date.now() + Math.min(minutes * 60_000, SB_MAX_LOCK_MS), breakEndsAt: null } };
  });
}

// Never past 3 days from now (the app: 7).
function sbExtendLock(minutes) {
  return sbUpdateSettings((s) => {
    const state = sbLockState(s);
    if (!state.locked || state.breaking) throw new Error("Not locked.");
    return { lock: { ...s.lock, endsAt: Math.min(s.lock.endsAt + minutes * 60_000, Date.now() + SB_MAX_LOCK_MS) } };
  });
}

// "Break lock early": everything stays on for 2 more hours, then the lock
// ends. Only offered while more than that is left.
function sbBreakLock() {
  return sbUpdateSettings((s) => {
    const state = sbLockState(s);
    if (!state.locked || state.breaking || state.lockLeftMs <= SB_BREAK_DELAY_MS) throw new Error("Can't break this lock early.");
    return { lock: { ...s.lock, breakEndsAt: Date.now() + SB_BREAK_DELAY_MS } };
  });
}

function sbCancelLockBreak() {
  return sbUpdateSettings((s) => ({ lock: s.lock ? { ...s.lock, breakEndsAt: null } : null }));
}

// The app's wording for lengths and time left.
function sbDescribeMinutes(minutes) {
  const days = Math.floor(minutes / 1440);
  const hours = Math.floor((minutes % 1440) / 60);
  const mins = minutes % 60;
  const parts = [];
  if (days) parts.push(days === 1 ? "1 day" : `${days} days`);
  if (hours) parts.push(hours === 1 ? "1 hour" : `${hours} hours`);
  if (mins) parts.push(mins === 1 ? "1 minute" : `${mins} minutes`);
  return parts.join(" ");
}

function sbRemaining(ms) {
  const minutes = Math.floor(ms / 60_000);
  if (minutes < 1) return "under a minute";
  if (minutes < 60) return `${minutes}m`;
  if (minutes < 1440) return `${Math.floor(minutes / 60)}h ${minutes % 60}m`;
  return `${Math.floor(minutes / 1440)}d ${Math.floor((minutes % 1440) / 60)}h`;
}

// --- Blocked sites ---

// The app's SiteName.Normalize: "reddit.com" blocks the site and all its
// subdomains, "youtube.com/shorts" just that part of it. A pasted address
// keeps only its site; a typed path is kept. Null if it isn't a site.
function sbNormalizeSite(text) {
  let site = String(text ?? "").trim().toLowerCase();
  let pasted = false;
  for (const scheme of ["https://", "http://"]) {
    if (site.startsWith(scheme)) {
      site = site.slice(scheme.length);
      pasted = true;
    }
  }
  site = site.split(/[?#]/)[0].replace(/\/+$/, "");
  if (pasted) site = site.split("/")[0];
  if (site.startsWith("www.")) site = site.slice(4);
  const host = site.split("/")[0];
  return host.includes(".") && !host.startsWith(".") && !host.endsWith(".") && !/\s/.test(site) ? site : null;
}

// Whether a blocked-sites entry covers this address.
function sbSiteCovers(site, url) {
  let parsed;
  try {
    parsed = new URL(url);
  } catch {
    return false;
  }
  if (parsed.protocol !== "http:" && parsed.protocol !== "https:") return false;
  const [host, ...rest] = site.split("/");
  const hostname = parsed.hostname.toLowerCase();
  if (hostname !== host && !hostname.endsWith(`.${host}`)) return false;
  const path = rest.join("/");
  return !path || parsed.pathname.toLowerCase().startsWith(`/${path}`);
}

// --- YouTube channels ---

function sbSameName(a, b) {
  return !!a?.trim() && !!b?.trim() && a.trim().toLowerCase() === b.trim().toLowerCase();
}

// The service's FindRule: an entry with an id matches by id only (so a
// copycat channel can't borrow a decision), a name-only entry by the
// channel name shown under the video.
function sbFindChannel(channels, channelId, channelName) {
  return (channelId && channels.find((c) => c.id === channelId))
    || channels.find((c) => !c.id && sbSameName(c.name, channelName))
    || null;
}

// The service's SetChannelRule, minus its lock rules (standalone has no
// lock). decision null removes the channel. Blocking by id also catches a
// name-only entry for the same channel.
function sbSetChannel(channels, { id, name }, decision) {
  const isTarget = id
    ? decision === "Blocked" && name?.trim()
      ? (c) => c.id === id || (!c.id && sbSameName(c.name, name))
      : (c) => c.id === id
    : (c) => sbSameName(c.name, name);
  if (!decision) return channels.filter((c) => !isTarget(c));
  if (!channels.some(isTarget)) return [...channels, { id: id ?? null, name: name?.trim() ?? "", decision }];
  return channels.map((c) => (isTarget(c) ? { ...c, decision, name: id && name?.trim() ? name.trim() : c.name } : c));
}

// --- Hide on YouTube ---

// The app's list (MainWindow.DeclutterItems): the user's order and wording.
// Covered: the parent hides the whole area this one is in, so it shows as on
// while the parent is on.
const SB_DECLUTTER_ITEMS = Object.freeze([
  { option: "HomeFeed", label: "Hide home feed" },
  { option: "RedirectHomeToSubscriptions", label: "Redirect to Subscriptions", parent: "HomeFeed" },
  { option: "VideoSidebar", label: "Hide video sidebar" },
  { option: "RecommendedVideos", label: "Hide recommended", parent: "VideoSidebar", covered: true },
  { option: "LiveChat", label: "Hide live chat", parent: "VideoSidebar", covered: true },
  { option: "Playlists", label: "Hide playlist", parent: "VideoSidebar", covered: true },
  { option: "Fundraiser", label: "Hide fundraiser", parent: "VideoSidebar", covered: true },
  { option: "EndScreenSuggestions", label: "Hide end screen feed" },
  { option: "EndScreenCards", label: "Hide end screen cards" },
  { option: "Shorts", label: "Hide Shorts" },
  { option: "Comments", label: "Hide comments" },
  { option: "Mixes", label: "Hide mixes" },
  { option: "MerchTicketsOffers", label: "Hide merch, tickets, offers" },
  { option: "VideoInfo", label: "Hide video info" },
  { option: "TopHeader", label: "Hide top header" },
  { option: "Notifications", label: "Hide notifications", parent: "TopHeader", covered: true },
  { option: "InaptSearchResults", label: "Hide inapt search results" },
  { option: "TrendingExplore", label: "Hide explore, trending" },
  { option: "MoreFromYouTube", label: "Hide more from YouTube" },
  { option: "SubscriptionsFeed", label: "Hide subscriptions" },
  { option: "Autoplay", label: "Disable autoplay" },
  { option: "Annotations", label: "Disable annotations" },
]);
