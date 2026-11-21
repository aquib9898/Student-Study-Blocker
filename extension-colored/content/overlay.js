// The in-page block screen and grey-zone popup (Milestone 5). Loads in the
// same content-script group as youtube.js (see manifest.json), which calls
// into window.SBOverlay — kept explicit rather than relying on the two
// files silently sharing one global scope. Renders only; it has no
// classifier or service logic of its own, same rule as youtube.js.

(() => {
  // The colored copy's colours for what it draws on YouTube: the block
  // screen, the grey-zone popup and the miniplayer notice. The popup and the
  // blocked-site page take theirs from theme.css; these can't, since they're
  // drawn inside YouTube's own page. Same palette: the LockIn logo's blues,
  // softened.
  const THEME = {
    // Light theme from Tinta (seed #4aa6ff), flat, no gradient: a pale-sky
    // screen so the solid buttons are the focus. Text pairs are WCAG AA+.
    screen: "rgba(242, 248, 255, 0.97)", // behind the block screen and popup
    glow: "transparent",               // no glow
    text: "#0f2238",                   // 15:1 on the screen
    textSoft: "#3e5a7a",               // 6.7:1
    mark: "#0060a9",                   // the logo
    markGlow: "transparent",
    // Split-complementary accents: petrol teal = blue's neighbour,
    // rust = its warm split-complement.
    allow: "#006a6e",                  // Allow channel (petrol teal)
    allowText: "#ffffff",              // 6.4:1 on the teal
    block: "#8c4e37",                  // Block channel (rust)
    blockText: "#ffffff",              // 6.4:1 on the rust
    notice: "#ffffff",                 // the miniplayer notice
    noticeBorder: "#bedfff",
    shadow: "rgba(15, 34, 56, 0.18)",
  };

  const OVERLAY_ID = "sb-overlay-root";

  function hide() {
    document.getElementById(OVERLAY_ID)?.remove();
  }

  function render(bodyHtml) {
    hide();
    const root = document.createElement("div");
    root.id = OVERLAY_ID;
    Object.assign(root.style, {
      position: "fixed",
      inset: "0",
      zIndex: "2147483647",
      display: "flex",
      alignItems: "center",
      justifyContent: "center",
      background: THEME.screen,
      color: THEME.text,
      font: "15px/1.5 system-ui, sans-serif",
      textAlign: "center",
    });
    root.innerHTML = `<div style="max-width:440px;padding:32px">${bodyHtml}</div>`;
    document.documentElement.appendChild(root);
    return root;
  }

  function escapeHtml(value) {
    return String(value).replace(/[&<>"']/g, (c) => ({
      "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;",
    })[c]);
  }

  // Matches the page shown for a blocked site (blocked.html): the logo's
  // shape in white, then "Blocked by ...". The logo comes from background.js
  // as a PNG data URL; anything else is left out rather than put in the page.
  function showBlockScreen(reason, logo) {
    const mark = typeof logo === "string" && /^data:image\/png;base64,[A-Za-z0-9+/=]+$/.test(logo)
      ? `<div style="filter:drop-shadow(0 0 12px ${THEME.markGlow})"><div style="width:96px;height:96px;margin:0 auto 20px;background:${THEME.mark};-webkit-mask:url(${logo}) center/contain no-repeat;mask:url(${logo}) center/contain no-repeat"></div></div>`
      : "";
    render(`
      ${mark}
      <div style="font-size:20px;font-weight:600;margin-bottom:10px">Blocked by Smart Study Distraction Blocker</div>
      <div style="color:${THEME.textSoft};font-size:13px">${escapeHtml(reason || "classified as a distraction")}</div>
    `);
  }

  // onAnswer("allow" | "block") is called once, when a button is clicked.
  // Both answers apply to the whole channel, not just this video, so the
  // buttons say so and the channel is named. No channelId (shouldn't happen
  // on a real YouTube watch page, but the classifier can't act on a channel
  // it doesn't know) means there is nothing to record a decision against,
  // so the buttons are left off entirely rather than answering into a rule
  // that can't be saved.
  function showGreyPopup(reason, channelId, channelName, onAnswer) {
    const channel = escapeHtml(channelName || "this channel");
    const root = render(`
      <div style="font-size:34px;margin-bottom:10px">&#129300;</div>
      <div style="font-size:18px;font-weight:600;margin-bottom:10px">Is this video study, or a distraction?</div>
      <div style="color:${THEME.textSoft};font-size:13px;margin-bottom:16px">${escapeHtml(reason || "couldn't classify this one confidently")}</div>
      ${channelId
        ? `<div style="font-size:14px;margin-bottom:16px">Your answer applies to every video from <b>${channel}</b>.</div>
           <div style="display:flex;gap:12px;justify-content:center">
             <button id="sb-allow" style="padding:10px 18px;font:inherit;cursor:pointer;border:0;border-radius:6px;background:${THEME.allow};color:${THEME.allowText}">Allow channel</button>
             <button id="sb-block" style="padding:10px 18px;font:inherit;cursor:pointer;border:0;border-radius:6px;background:${THEME.block};color:${THEME.blockText}">Block channel</button>
           </div>`
        : `<div style="color:${THEME.textSoft};font-size:12px">No channel information on this video, so there's nothing to save a decision against. Staying paused.</div>`
      }
    `);
    root.querySelector("#sb-allow")?.addEventListener("click", () => onAnswer("allow"));
    root.querySelector("#sb-block")?.addEventListener("click", () => onAnswer("block"));
  }

  // A small notice for a video that was playing in the miniplayer (queue):
  // it has been stopped, and the page underneath stays usable, so this must
  // not cover it the way the block screen does. Removes itself.
  const NOTICE_ID = "sb-notice-root";
  function showNotice(text) {
    document.getElementById(NOTICE_ID)?.remove();
    const root = document.createElement("div");
    root.id = NOTICE_ID;
    Object.assign(root.style, {
      position: "fixed",
      left: "16px",
      bottom: "16px",
      maxWidth: "360px",
      zIndex: "2147483647",
      padding: "12px 16px",
      borderRadius: "10px",
      background: THEME.notice,
      border: `1px solid ${THEME.noticeBorder}`,
      color: THEME.text,
      font: "13px/1.45 system-ui, sans-serif",
      boxShadow: `0 4px 16px ${THEME.shadow}`,
    });
    root.innerHTML =
      `<div style="font-weight:600;margin-bottom:4px">Blocked by Smart Study Distraction Blocker</div>` +
      `<div style="color:${THEME.textSoft}">${escapeHtml(text)}</div>`;
    document.documentElement.appendChild(root);
    setTimeout(() => root.remove(), 8000);
  }

  window.SBOverlay = { showBlockScreen, showGreyPopup, showNotice, hide };
})();
