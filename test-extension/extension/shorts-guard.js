// YouTube switches to Shorts without a real page load, which the URLBlocklist
// policy never sees. Turning those switches into real loads lets the policy
// block them, so the policy stays the enforcer and this only triggers it.

console.info("[Study Blocker] Shorts guard active on", location.href);

function isShorts(url) {
  return new URL(url, location.href).pathname.startsWith("/shorts");
}

// Capture phase on window runs before YouTube's own click handling, so the
// Short never starts loading in-page. Modified/middle clicks already open a
// real page load in a new tab, which the policy blocks by itself.
window.addEventListener(
  "click",
  (event) => {
    if (event.button !== 0 || event.ctrlKey || event.metaKey || event.shiftKey || event.altKey) return;
    const link = event.composedPath().find((el) => el instanceof HTMLAnchorElement && el.href);
    if (!link || !isShorts(link.href)) return;
    event.preventDefault();
    event.stopImmediatePropagation();
    location.assign(link.href);
  },
  true
);

// Backstop for routes that don't come from a click (back/forward, autoplay,
// swipe). Only acts on changes after load, so a Shorts page that loaded for
// real (policy not applied) can't cause a reload loop.
let lastHref = location.href;
function checkUrl() {
  if (location.href === lastHref) return;
  lastHref = location.href;
  if (isShorts(location.href)) location.replace(location.href);
}
window.addEventListener("yt-navigate-finish", checkUrl);
window.addEventListener("popstate", checkUrl);
setInterval(checkUrl, 500);
