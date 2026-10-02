// Runs in the page's own JavaScript world (manifest "world": "MAIN"), because
// YouTube's player API isn't visible from the extension's isolated world.
// Reports what the player is actually playing to youtube.js. The page text
// can't be used for this: after an in-app navigation it keeps showing the
// previous video's title for a moment, while the address already has the new
// video's id.
(() => {
  const SOURCE = "study-blocker-page";
  let lastSent = null;

  function urlVideoId() {
    const url = new URL(location.href);
    if (url.pathname === "/watch") return url.searchParams.get("v");
    const parts = url.pathname.split("/");
    return parts[1] === "shorts" ? parts[2] ?? null : null;
  }

  function playerResponse() {
    for (const selector of ["#movie_player", "#shorts-player"]) {
      const response = document.querySelector(selector)?.getPlayerResponse?.();
      if (response?.videoDetails?.videoId) return response;
    }
    return null;
  }

  function check() {
    const id = urlVideoId();
    if (!id || id === lastSent) return;
    const response = playerResponse();
    const d = response?.videoDetails;
    // Right after a navigation the player can still hold the previous video;
    // report only once it matches the address.
    if (!d || d.videoId !== id) return;
    lastSent = id;
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
        isShort: location.pathname.startsWith("/shorts"),
        isLive: !!d.isLiveContent,
        lengthSeconds: Number(d.lengthSeconds) || null,
        keywords: Array.isArray(d.keywords) ? d.keywords.slice(0, 50) : [],
        description: (d.shortDescription ?? "").slice(0, 5000),
        url: location.href,
      },
    }, location.origin);
  }

  setInterval(check, 500);
  window.addEventListener("yt-navigate-finish", check);
})();
