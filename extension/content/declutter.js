// Milestone 6: hides whichever "algorithmic/temptation surfaces" are ticked
// in the service's Declutter checklist (plan §9), via a single injected
// stylesheet — never by removing nodes or blocking navigation, so search,
// subscriptions, playlists and an opened video all keep working (an opened
// video is still classified and enforced as usual; this only changes what's
// visible around it). background.js resolves the ticked options to selectors
// from data/declutter-selectors.json (see getDeclutter there for why this
// script can't read that file itself). No classifier or service logic of its
// own, same rule as youtube.js.
//
// Two options do a little more than hide (added 2026-11-21 with the rest of
// the user's YouTube list):
// - RedirectHomeToSubscriptions sends YouTube's home page to the
//   subscriptions feed (the one place this touches navigation);
// - Autoplay ("Disable autoplay") turns the player's own autoplay switch
//   off, besides hiding that switch so it stays off.
//
// Wrapped in an IIFE, like overlay.js, because this loads in the same
// content-script group as youtube.js (shared global scope, classic-script
// semantics) — youtube.js already declares its own top-level TAG, so this
// file must not add a second one at that same scope.
(() => {
  const TAG = "[Study Blocker]";
  const STYLE_ID = "sb-declutter-style";
  const SUBSCRIPTIONS = "/feed/subscriptions";

  // The ticked options as last heard from the service. Kept when the
  // service can't be asked, like the stylesheet.
  let active = new Set();

  // Null when the service couldn't be asked. Reloading the extension while
  // this tab was already open also leaves this script running against a
  // torn-down connection (same "Extension context invalidated" class of
  // issue as youtube.js's report()).
  function currentDeclutter() {
    return new Promise((resolve) => {
      try {
        chrome.runtime.sendMessage({ type: "get-declutter" }, (reply) => {
          resolve(chrome.runtime.lastError || !reply?.ok ? null : reply);
        });
      } catch {
        resolve(null);
      }
    });
  }

  // One rule per selector, so a selector this browser doesn't understand
  // (or a typo after a YouTube redesign) drops only itself, not every hide
  // in the list.
  function stylesheet(declutter) {
    const hides = declutter.selectors.map((selector) => `${selector} { display: none !important; }`);
    return [...hides, ...(declutter.css ?? [])].join("\n");
  }

  // A CSS rule keeps applying to nodes YouTube's SPA renders later, so one
  // injection covers every future in-app navigation — no need to reapply on
  // yt-navigate-finish the way page-bridge.js and shorts-guard.js do.
  let shown = "";
  let lastCheck = 0;
  async function apply() {
    lastCheck = Date.now();
    const declutter = await currentDeclutter();
    // Couldn't ask: whatever is hidden stays hidden.
    if (!declutter) return;
    active = new Set(declutter.options);
    redirectHome();
    autoplayOff();

    const css = stylesheet(declutter);
    if (css === shown) return;
    shown = css;

    document.getElementById(STYLE_ID)?.remove();
    if (!css) {
      console.info(TAG, "Declutter: nothing hidden");
      return;
    }
    const style = document.createElement("style");
    style.id = STYLE_ID;
    style.textContent = css;
    document.documentElement.appendChild(style);
    console.info(TAG, "Declutter on:", declutter.options.join(", "));
  }

  // --- Redirect home to Subscriptions ---

  // youtube.com itself only: this script also runs on music.youtube.com and
  // other YouTube hosts, whose "/" is something else.
  function onMainSite() {
    return location.hostname === "www.youtube.com" || location.hostname === "youtube.com";
  }

  function isHome(url) {
    return new URL(url, location.href).pathname === "/";
  }

  function redirectHome() {
    if (!active.has("RedirectHomeToSubscriptions") || !onMainSite() || !isHome(location.href)) return;
    console.info(TAG, "Home feed: going to Subscriptions");
    location.replace(SUBSCRIPTIONS);
  }

  // The logo and the Home entry go to "/" inside YouTube's single-page app.
  // Catching the click before YouTube does (capture phase on window, like
  // shorts-guard.js) means the home feed never starts loading.
  window.addEventListener(
    "click",
    (event) => {
      if (!active.has("RedirectHomeToSubscriptions") || !onMainSite()) return;
      if (event.button !== 0 || event.ctrlKey || event.metaKey || event.shiftKey || event.altKey) return;
      const link = event.composedPath().find((el) => el instanceof HTMLAnchorElement && el.href);
      if (!link || new URL(link.href).hostname !== location.hostname || !isHome(link.href)) return;
      event.preventDefault();
      event.stopImmediatePropagation();
      location.assign(SUBSCRIPTIONS);
    },
    true
  );

  // --- Disable autoplay ---

  // The switch is a button whose inner part carries aria-checked; clicking
  // it works even while the stylesheet hides it (checked on live YouTube
  // 2026-11-21). YouTube remembers the choice, so once off it mostly stays
  // off. The pause between clicks stops a slow redraw from flipping it back.
  let lastAutoplayClick = 0;
  function autoplayOff() {
    if (!active.has("Autoplay") || Date.now() - lastAutoplayClick < 2000) return;
    const on = document.querySelector('.ytp-autonav-toggle-button[aria-checked="true"]');
    const button = on?.closest("button");
    if (!button) return;
    lastAutoplayClick = Date.now();
    button.click();
    console.info(TAG, "Autoplay switched off");
  }

  // --- Keeping up with the page ---

  apply();

  // In-app navigation: YouTube never reloads the page, and a new player can
  // come with autoplay on. Also every second, cheaply, because the player's
  // switch appears some time after the page does (and back/forward to the
  // home page fires no click).
  function onPageChange() {
    redirectHome();
    autoplayOff();
  }
  window.addEventListener("yt-navigate-finish", onPageChange);
  window.addEventListener("popstate", onPageChange);
  setInterval(onPageChange, 1000);

  // Coming back to this tab (from the desktop app, say) picks up what
  // changed there meanwhile, without a reload: a Declutter box ticked, or
  // the extension switched off or on. At most every few seconds, like
  // youtube.js's re-check.
  function reapply() {
    if (Date.now() - lastCheck >= 3000) apply();
  }
  window.addEventListener("focus", reapply);
  document.addEventListener("visibilitychange", () => {
    if (!document.hidden) reapply();
  });

  // Standalone mode (no desktop app): a switch flipped in the toolbar popup
  // applies at once, the popup still open over this tab.
  try {
    chrome.storage.onChanged.addListener((changes, area) => {
      if (area === "local" && changes.standalone) apply();
    });
  } catch {
    // Extension context invalidated: a stale tab, nothing to listen for.
  }
})();
