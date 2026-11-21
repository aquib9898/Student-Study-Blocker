// Runs in the page's own JavaScript world (manifest "world": "MAIN"), because
// YouTube's player API isn't visible from the extension's isolated world.
// Reports what each player is actually playing to youtube.js. The page text
// can't be used for this: after an in-app navigation it keeps showing the
// previous video's title for a moment, while the address already has the new
// video's id.
//
// The address bar is NOT enough on its own. YouTube plays videos in players
// that have nothing to do with the address:
// - "Add to queue" plays in the miniplayer (#movie_player, moved into
//   ytd-miniplayer) while the address stays on the search or home page;
// - hovering a thumbnail plays it in #inline-preview-player, sound included
//   once the viewer unmutes it;
// - a channel page autoplays its trailer.
// So two kinds of report go out:
// - the address video (offPage: false), from the main player, once the
//   player's video matches the address (the original behaviour);
// - any other player that is actually playing (offPage: true, with its
//   playerId), so youtube.js can stop just that player.
(() => {
  const SOURCE = "study-blocker-page";
  const FROM_CONTENT = "study-blocker-content";
  const MAIN_PLAYERS = ["movie_player", "shorts-player"];
  // YouTube's player states: 1 playing, 3 buffering.
  const ACTIVE_STATES = [1, 3];

  let lastAddress = null;
  let sentMain = null;
  // "playerId|videoId" for off-page videos reported and still playing. An
  // entry is dropped as soon as that player stops playing it, so starting it
  // again (or the preview replaying on the next hover) is judged again.
  const sentOffPage = new Set();
  let anonymous = 0;

  function urlVideoId() {
    const url = new URL(location.href);
    if (url.pathname === "/watch") return url.searchParams.get("v");
    const parts = url.pathname.split("/");
    return parts[1] === "shorts" ? parts[2] ?? null : null;
  }

  function attempt(fn) {
    try { return fn(); } catch { return undefined; }
  }

  // Every YouTube player on the page, each with an id youtube.js can hand
  // back to stop it (players without an element id get a data attribute).
  function players() {
    const found = new Set(document.querySelectorAll("#movie_player, #shorts-player, .html5-video-player"));
    return [...found].filter((p) => typeof p.getPlayerResponse === "function");
  }

  function playerKey(p) {
    if (p.id) return p.id;
    if (!p.dataset.sbPlayer) p.dataset.sbPlayer = `sb-player-${++anonymous}`;
    return p.dataset.sbPlayer;
  }

  // Playing, by the player's own state or by its <video> actually running.
  // The second matters during a pre-roll ad: the state stays -1
  // ("unstarted") for the whole ad while the player already holds the queued
  // video, so going by the state alone let the ad run before anything was
  // judged (seen in the miniplayer, 2026-11-19).
  function isActive(p) {
    if (ACTIVE_STATES.includes(attempt(() => p.getPlayerState()))) return true;
    return [...p.querySelectorAll("video")].some((v) => !v.paused && !v.ended);
  }

  function findPlayer(key) {
    return document.getElementById(key) ?? document.querySelector(`[data-sb-player="${CSS.escape(key)}"]`);
  }

  function post(response, id, extra) {
    const d = response.videoDetails;
    window.postMessage({
      source: SOURCE,
      meta: {
        id,
        title: d.title ?? "",
        channel: d.author ?? null,
        channelId: d.channelId ?? null,
        // The category the uploader picked ("Education", "Entertainment",
        // "Science & Technology", ...). Coarse and often misleading, so the
        // classifier treats it as one signal among several.
        category: response.microformat?.playerMicroformatRenderer?.category ?? null,
        isShort: !extra.offPage && location.pathname.startsWith("/shorts"),
        isLive: !!d.isLiveContent,
        lengthSeconds: Number(d.lengthSeconds) || null,
        keywords: Array.isArray(d.keywords) ? d.keywords.slice(0, 50) : [],
        description: (d.shortDescription ?? "").slice(0, 5000),
        url: location.href,
        ...extra,
      },
    }, location.origin);
  }

  function check() {
    const address = urlVideoId();
    // A new address is a new chance to judge: opening a video that was
    // stopped in the miniplayer must be classified again.
    if (address !== lastAddress) {
      lastAddress = address;
      sentMain = null;
      sentOffPage.clear();
    }

    const playing = new Set();
    for (const p of players()) {
      const response = attempt(() => p.getPlayerResponse());
      const d = response?.videoDetails;
      if (!d?.videoId) continue;
      const key = playerKey(p);

      if (address && MAIN_PLAYERS.includes(p.id)) {
        // Right after a navigation the player can still hold the previous
        // video; report only once it matches the address.
        if (d.videoId !== address || sentMain === address) continue;
        sentMain = address;
        post(response, address, { offPage: false, playerId: key });
        continue;
      }

      // Anything else counts only while it is really playing: a stopped or
      // paused one is judged when it starts.
      if (!isActive(p)) continue;
      const sent = `${key}|${d.videoId}`;
      playing.add(sent);
      if (sentOffPage.has(sent)) continue;
      sentOffPage.add(sent);
      post(response, d.videoId, {
        offPage: true,
        playerId: key,
        inMiniplayer: !!p.closest("ytd-miniplayer"),
      });
    }
    for (const sent of sentOffPage) if (!playing.has(sent)) sentOffPage.delete(sent);
  }

  // Stops one player, if it still holds that video. The miniplayer is closed
  // as well, which also drops the queue.
  function stop(key, videoId) {
    const p = findPlayer(key);
    if (!p) return;
    const current = attempt(() => p.getPlayerResponse())?.videoDetails?.videoId;
    if (videoId && current && current !== videoId) return;
    attempt(() => p.pauseVideo());
    attempt(() => p.stopVideo());
    for (const v of p.querySelectorAll("video")) v.pause();
    if (p.closest("ytd-miniplayer")) {
      const close = [...p.querySelectorAll(".ytp-miniplayer-close-button")].find((b) => b.offsetParent !== null)
        ?? p.querySelector(".ytp-miniplayer-close-button");
      close?.click();
    }
    sentOffPage.delete(`${key}|${videoId}`);
  }

  window.addEventListener("message", (event) => {
    if (event.source !== window || event.data?.source !== FROM_CONTENT) return;
    if (event.data.cmd === "recheck") {
      sentMain = null;
      sentOffPage.clear();
      check();
    } else if (event.data.cmd === "stop" && typeof event.data.playerId === "string") {
      stop(event.data.playerId, event.data.videoId);
    }
  });

  // A video starting to play is the moment to judge it, not the next tick
  // of the poll. The player's state lags the media event slightly.
  document.addEventListener("play", () => setTimeout(check, 60), true);
  document.addEventListener("playing", () => setTimeout(check, 0), true);

  setInterval(check, 400);
  window.addEventListener("yt-navigate-finish", check);
})();
