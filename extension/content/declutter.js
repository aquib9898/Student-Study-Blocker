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
// Wrapped in an IIFE, like overlay.js, because this loads in the same
// content-script group as youtube.js (shared global scope, classic-script
// semantics) — youtube.js already declares its own top-level TAG, so this
// file must not add a second one at that same scope.
(() => {
  const TAG = "[Study Blocker]";
  const STYLE_ID = "sb-declutter-style";

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
    const css = declutter.selectors.length ? `${declutter.selectors.join(",\n")} {\n  display: none !important;\n}` : "";
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
    console.info(TAG, "Declutter hiding:", declutter.options.join(", "));
  }

  apply();

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
})();
