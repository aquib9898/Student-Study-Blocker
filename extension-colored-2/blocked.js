// Names the blocked site in the tab title; the page itself only says it is blocked. (Extension pages
// can't run inline scripts, hence this file.)
const site = (new URLSearchParams(location.search).get("site") ?? "").replace(/^www\./, "");
if (site) {
  document.title = `${site} is blocked`;
}
