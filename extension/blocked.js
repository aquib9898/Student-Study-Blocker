// Names the blocked site; the rest of blocked.html is static. (Extension pages
// can't run inline scripts, hence this file.)
const site = (new URLSearchParams(location.search).get("site") ?? "").replace(/^www\./, "");
if (site) {
  document.getElementById("site").textContent = site;
  document.title = `${site} is blocked`;
}
