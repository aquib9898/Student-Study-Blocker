# extension — the companion extension (Milestones 3–9)

The Chromium (MV3) extension. It reads YouTube video metadata, has the
service classify it, pauses the player while a verdict is pending, shows the
block screen and applies Declutter.
- Milestone 3 built the channel to the service, metadata reading and the
  pause capability.
- Milestone 4 wired each video's metadata to the service's classifier.
- Milestone 5 turned the verdict into the actual in-page block screen and
  grey-zone popup.
- Milestone 6 added Declutter, hiding YouTube's own feed and recommendation
  surfaces.
- Milestone 7 moved every setting into the desktop app (`../app/`). The
  extension only enforces them. Its one control, since 0.11.0, is **Block
  channel** in the toolbar popup (see "Block channel" below).
- After Milestone 8 the app gained an **Extension on / off** switch (see
  "Switched off" below), and an open YouTube tab now re-checks when you
  come back to it.

Plain JavaScript, so there's no build step. Every browser runs a store copy
that the service force-installs: Edge Add-ons for Edge (Hidden, approved
2026-10-04), the Chrome Web Store for Chrome and Brave (unlisted, published
2026-10-06). Loading this folder unpacked is only for quick tests (see
"Local setup").
It can move to TypeScript later without changing the design.

## What's here

- `manifest.json`: MV3. A fixed `key` pins the unpacked extension's ID to
  `dkbfaenfbikncbcgecpekmpgcemijgob`, so the native-messaging host can list
  it in `allowed_origins` before the extension is ever loaded. The store
  package leaves the key out, and Edge Add-ons gave its copy its own ID,
  `jfaoannlngkcfncopcnofjiooamhfffm`, and the Chrome Web Store draft
  `fgflnbhgcppnodpggmbeomdpciebgaln`. The host allows all three (installer
  `common.ps1`).
- `background.js`: the only part that talks to the service. It reaches the
  SYSTEM service through the native-messaging host.
  - It pulls status and rules on a 1-minute alarm and whenever the popup
    opens.
  - It sends every reported video to the service's classifier and tells the
    content script what to show for the verdict.
  - It relays the grey-zone popup's answer back to `set-channel-rule` (see
    "How a video gets classified" below).
  - It remembers the last lock state the service reported (session
    storage), for the fail-safe when the service can't be reached.
- `content/shorts-guard.js`: the proven Milestone 0 guard that turns in-app
  Shorts navigations into real page loads so the URL-blocklist policy
  catches them.
- `content/page-bridge.js`: runs in the page's own JavaScript world
  (`"world": "MAIN"`) and asks YouTube's player what it's actually playing:
  id, title, channel, channel id, description, keywords, length, live or
  not. It reports only once the player's video matches the address. Reading
  the page text instead gave the *previous* video's title after every
  in-YouTube click, because YouTube updates the address before the text.
  It also watches every other YouTube player on the page and reports any
  that is actually playing as `offPage: true` with its `playerId`: the
  miniplayer after "Add to queue" (`#movie_player` moved into
  `ytd-miniplayer`, address still on search/home), hover previews
  (`#inline-preview-player`) and channel trailers (`#c4-player`). None of
  those has a `/watch` address, so before 0.11.5 they played unjudged.
  Playing means the player's state is playing/buffering *or* its `<video>`
  is running: during a pre-roll ad the state stays -1. Every verdict now
  names its video, and a blocked (or grey) off-page one is stopped in its
  own player instead of getting the page-wide block screen: the miniplayer
  is also closed (with a small notice), a hover preview just stops. It is
  remembered for a minute, so a preview restarting on the next mouse move
  is stopped at once, and judged again whenever it starts playing again.
  Checked 2026-11-19 against live YouTube with a fake background that
  blocked everything: queue (with and without an ad), hover preview,
  channel trailer, Study verdict leaves a preview playing, watch page
  unchanged.
- `content/overlay.js`: renders the in-page block screen and grey-zone
  popup as a full-page overlay, exposed to `content/youtube.js` as
  `window.SBOverlay` (both load in the same content-script group — see
  `manifest.json` — so this is an explicit hand-off rather than the two
  files silently sharing one global scope). No classifier or service logic
  of its own, same as `youtube.js`.
- `content/youtube.js`: receives metadata from the bridge and passes it to
  the background worker; pauses/resumes the player and shows or hides the
  overlay on command. While the block screen or popup is up, it pauses any
  attempt to play straight back. The overlay only covers the page, so
  YouTube's own keyboard shortcuts (k, space) and the keyboard's media keys
  could otherwise still start the video underneath (fixed in Milestone 7).
- `content/declutter.js`: asks the background worker for the selectors to
  hide, on page load and again whenever you come back to the tab, and
  injects them as one `<style>` tag. CSS hides, no node removal and no
  navigation blocking, apart from two options that act on the page
  (Redirect to Subscriptions, Disable autoplay) — see "How Declutter hides
  things" below.
- `data/declutter-selectors.json`: the DOM selectors for each Declutter
  option, read by `background.js` (not the content script — see below), so
  a YouTube redesign is a data edit, not a code change.
- `blocked.html` / `blocked.js`: the page shown for a blocked site, in
  place of the browser's own (see "Blocked sites" below).
- `images/logo-mark.png`: the logo's shape alone (white on transparent),
  drawn in the text colour on `blocked.html` and in white on the YouTube
  block screen. `icons/` holds the toolbar icons: the same logo on a black
  rounded square, as in the app's program icon.
- `popup.html` / `popup.js`: the toolbar popup, with two faces since
  0.12.0 (the user's request, 2026-11-21):
  - **The desktop app is installed** (the native host answers): no
    controls at all, just "Control from App" and, under it, "App
    connected" (green) or "App not connected" (red, the browser's error
    as its tooltip). Every setting lives in the app. The old status rows
    and the popup's Block channel button are gone.
  - **No desktop app** ("Specified native messaging host not found", which
    Chrome also says when a host is registered but its program is gone, as
    for a few minutes after the Microsoft Store version is uninstalled):
    standalone mode, see "Standalone mode" below. No connected/disconnected
    line. Top to bottom: the YouTube section, then Blocked sites. (The app
    pitch that used to close the popup was removed at the user's request,
    2026-11-21, as was the "only stricter during a lock" note.)
  - Always light, whatever the browser's or Windows' theme. Every colour is
    in `theme.css` (and `THEME` at the top of `content/overlay.js` for what
    is drawn on YouTube): neutral greys, brand blue #2563c9, coral #b24d39
    for "blocked", amber for warnings (picked with Tinta, 2026-11-21; the
    Windows app uses the same palette).
  - Block screens say only "Blocked by Smart Study Distraction Blocker":
    no site name, score or category (the user's call, 2026-11-21).
- `settings.js`: standalone mode's settings (chrome.storage.local, key
  `standalone`) and their helpers, shared by the popup and `background.js`.
- `classifier.js` + `data/classifier-words.json`: standalone mode's copy of
  the service's video classifier (see "Standalone mode").
- `native-host/`: the bridge to the service (below).

## The native-messaging host

An MV3 extension can't open a pipe to a Windows service. The browser can,
though, start a small "native-messaging host" process as the current user
and talk to it over stdin/stdout. `../service/src/StudyBlocker.NativeHost`
is that bridge: it reads one framed message from the browser, relays it to
the service's named pipe (reusing the service's own IPC client), and writes
the reply back. One message in, one out — matching how the service answers
one request per connection.

This means the extension speaks the same request types as the service
(`ping`, `get-status`, …); the host just carries them across the
stdio↔pipe boundary. It grants no new power: the host runs as the same
Standard user the browser does, and the service still decides what any
caller is allowed to do.

## How a video gets classified

`content/page-bridge.js` reports a video's metadata (title, channel,
category, description, keywords, …) to `content/youtube.js`, which forwards
it to `background.js`. From there:

1. `background.js` sends it to the service as `classify-video` and gets
   back `{Verdict, Reason, Source}` — see `../service/README.md`'s
   Classifier section for how the service decides.
2. The verdict is logged in the service worker's own console (see "Trying
   it" below) and enforced, **locked or not**. Plan §10: outside a lock
   "the same rules still apply ... but everything is freely editable"; a
   lock only stops the rules being loosened. (Up to Milestone 7 this was
   lock-only, so outside a lock distractions were just logged. That wasn't
   what the plan or the user meant, and it was found live on 2026-09-28.)
3. **Grey** shows the in-page popup, so channels get sorted as you go.
   - The buttons are "Allow channel" and "Block channel", and the popup
     names the channel. Either answer applies to every video from that
     channel, not just this one.
   - Either button calls `set-channel-rule` through `background.js`, with
     the channel's id and name and this video's id.
   - **Allow** resumes the player immediately. During a lock the service
     only accepts it because the video's own verdict was Grey (see
     `../service/README.md`, "Channel lists during a lock").
   - **Block** turns the popup into the block screen, which stays up.
4. **Distraction** shows the block screen, which stays up.
5. **Study** resumes the player if something had paused it.
6. If the service can't be reached at all, the block screen shows anyway
   if the last state the service reported was locked ("couldn't reach the
   classifier while locked"). That's plan §17's fail-safe: uncertainty
   during a lock is never a silent allow. Outside a lock there's nothing to
   go on, so the video plays.

The block screen and grey-zone popup are full-page overlays
(`content/overlay.js`), not just the player pausing silently. See
`../service/README.md`'s "Channel lists during a lock" section for the
ratchet behind the popup's answers: a blocked channel can't be re-allowed
until the lock ends, and an Allow answer adds the channel to the "Allowed
during this lock" list.

Enforcement runs when a video loads, and again when you come back to its
tab (the window regaining focus, or switching to that tab), at most every
3 seconds. So a rule changed in the app while a video is open, such as
blocking its channel or switching the extension off, applies as soon as you
click back into the browser, with no reload.
- Only the video in the address bar is re-checked, so a page with no video
  asks nothing.
- A re-check never starts a video you paused yourself: the player is only
  resumed if the extension was the one holding it paused.

## Blocked sites

Sites on the app's blocked list, and Shorts during a lock, are blocked by
browser policy (the service's `URLBlocklist`), not by the extension, so they
stay blocked even if the extension is broken or removed (plan §17). The
app's switch does lift them, through the service (see "Switched off"). The browser then shows its own page, Edge's "This page is blocked /
Your organization doesn't allow you to view this site", which no policy can
change.

So `background.js` replaces it. When `webNavigation` reports a tab's main
frame failed with `net::ERR_BLOCKED_BY_ADMINISTRATOR`, it sends the tab to
`blocked.html?site=<host>`: the logo and "Blocked by Smart Study
Distraction Blocker", with the site underneath, in the app's own colours,
light or dark.
- This is only how the block looks. The browser has already blocked the
  address by then, and does so whether or not the extension runs.
- **Back** from `blocked.html` would land on the blocked address again (the
  entry before it), get blocked, and come straight back. So when a blocked
  navigation starts from `blocked.html` for the same site, the tab goes back
  one step further instead. With nothing further back, `blocked.html` just
  shows again.
- It needs the `webNavigation` permission (Chrome words it "Read your
  browsing history"), since the extension otherwise sees only YouTube. It
  reads nothing but these errors.
- The YouTube block screen (`content/overlay.js`) uses the same logo and
  heading. The logo reaches it from `background.js` as a data URL: a
  content script can't load the extension's own files without declaring them
  web-accessible, which would let any site detect the extension.
- Verified with `blocked-page-test.js` (in Claude's session scratchpad), in
  Chrome and Edge: the page and its logo, Back, a blocked site opened in a
  new tab, and the YouTube block screen.

## Standalone mode (no desktop app)

Since 0.12.0 (the user's request, 2026-11-21) the extension works on its
own when the app isn't installed: the browser then has no native-messaging
host, `callService` flags that as `appMissing`, and every handler in
`background.js` falls back to the extension's own settings. The popup holds
the same sections as the app minus **Blocked apps** and education-only
mode (removed at the user's request, 2026-11-21): the Lock, YouTube
channels (Allow / Block, Enter blocks), the 22 "Hide on YouTube" switches
and blocked sites, plus an on/off switch. A switch with sub-options (home
feed, video sidebar, top header) has a "+" at the end of its row; its
sub-options stay folded away until it's clicked ("−" folds them again),
folded every time the popup opens. Nothing enforces any of it from outside
the browser.
- **The lock** (the user's request, 2026-11-21): the app's Lock, rules and
  wording included, with two differences (the user's choice): no preset
  buttons, Lock shows only the slider (hour by hour to a day, then every 6
  hours), and it stops at 3 days, not 7. "Lock for …?" is asked in the
  popup's own box. Locked, the header says "Locked · X left", Lock becomes
  Extend (never past 3 days left), and things can only
  get stricter: no remove buttons on sites or blocked channels, no Allow,
  a switch that's on stays on (turning one on asks first), the on/off
  switch stays on (starting a lock switches it on). **Break lock early**
  (offered while more than 2 hours are left) ends it after a 2-hour
  countdown, cancellable. `sbUpdateSettings` refuses any loosening
  change during a lock (`sbAtLeastAsStrict`), except a grey-zone popup
  Allow, which the app also takes during a lock. State: `lock: { endsAt,
  breakEndsAt }` in the settings, on the PC's clock.
  - It is breakable on purpose (the user accepted it): switching the
    extension off or removing it in the browser's extensions page ends it,
    and so would changing the PC's clock. The app is what makes a lock
    unbreakable (it force-installs the extension through browser policy),
    which is the extension's description and the popup's last line.
- **Blocked sites**: one `declarativeNetRequest` dynamic "block" rule per
  site (main page and frames; `reddit.com` covers its subdomains,
  `youtube.com/shorts` just that path), kept in step by `syncSiteRules` on
  every settings change and every status refresh. A redirect would need
  host permissions for every site, so the browser shows its own error for
  a moment and the `onErrorOccurred` listener swaps in `blocked.html`, as
  for the app's policy blocks. `ERR_BLOCKED_BY_CLIENT` also comes from ad
  blockers, so it only counts while we have rules and one of our sites
  covers the address. With the app installed there are no rules: its
  browser policy does the blocking.
- **YouTube verdicts**: `classifier.js` is a port of the service's
  `ClassifierStore` + `RuleScoring`, in the same order: channel lists,
  verdict cache (1000 videos), weighted score with the channel's learned
  bias. No education-only gate (that mode is the app's alone). Its word lists are in
  `data/classifier-words.json`; the service test
  `DeclutterTests.ClassifierWordsMatchTheService` fails when they drift
  from `RuleScoring.cs`. The grey-zone popup's answer goes into the
  extension's channel list.
- **Hide on YouTube**: `getDeclutter` reads the ticked options from the
  settings; `declutter.js` also re-applies on the storage change, so a
  switch flipped in the popup shows on the tab at once.
- Any settings change makes every open YouTube tab check its videos again
  (`recheck-video`).
- Over a YouTube video the popup also shows the video's channel with a
  **Block channel** button (below), standalone mode only.
- An installed app whose service is down is NOT standalone: the app's
  rules and fail-safe still apply, and the popup says "App not connected".
- The standalone settings are left in storage when the app is installed,
  unused; they come back if the app is uninstalled.
- `declarativeNetRequest` is a new permission in 0.12.0 ("Block content on
  any page"): force-installed copies get it silently; a copy someone added
  by hand from a store is disabled by the browser until they accept it.

## Block channel

Standalone mode only since 0.12.0 (with the app installed the popup has no
controls). The toolbar popup's one control before that (the user's request, 2026-09-30). Over a
YouTube video (a watch page or a Short) it shows the video's channel and a
red **Block channel** button, which adds that channel to the blocked list
straight away, with no question.
- The popup asks the tab's `youtube.js` which video is on screen (only if
  it's still the one in the address bar), then saves the channel (id and
  name) to the standalone channel list, like the grey-zone popup's Block.
- Every open YouTube tab then checks its video again, so the one on screen,
  and any other tab showing that channel, gets the block screen at once.
- Before 0.12.0 it was the one control the extension had, because blocking
  only ever makes things stricter: the service accepted it at any time,
  lock or not. Now it writes the standalone channel list instead.
- Already blocked, the popup says "This channel is blocked." instead. On the
  allowed list, it says so and still offers the button (blocking takes it
  off that list). Away from a video it says to open one.
- Channel pages (`youtube.com/@name`) aren't covered: open one of the
  channel's videos.

## Switched off

The app's **Extension on / off** switch is `RuleSet.ExtensionEnabled` on the
service (see `../service/README.md`). While it's off:
- The service answers every video with Study, source `switched-off`,
  without judging it. So nothing is blocked, and a block screen or popup
  already showing lifts on the next re-check.
- `background.js` gives `declutter.js` no selectors, so nothing is hidden.
- The service lifts the blocked sites' policy (since 2026-10-01; before
  that they stayed blocked), so the page for blocked sites never shows.
  `content/shorts-guard.js` keeps running, but it only helps that policy
  catch Shorts, so with nothing blocked a Short just loads.
- The toolbar popup says "Switched off in the app: nothing is blocked", and
  "off" for blocked sites. **Block channel** still saves the channel, and
  says it applies once the extension is switched back on.

It can't be off during a lock: the service refuses it, and starting a lock
switches it back on.

## How Declutter hides things

Plan §9's checklist (home feed, recommended videos, end-screen suggestions,
autoplay, comments, trending/explore, live chat, subscriptions feed) lives on
the service as `RuleSet.Declutter` (see `../service/README.md`'s "YouTube
Declutter" section for the ratchet backing it) — the extension only renders
whatever is ticked, exactly like it has no opinion on classifier weights.

1. On every YouTube page load, and whenever you come back to the tab,
   `content/declutter.js` sends `get-declutter` to `background.js`, which
   reads the ticked options from `get-status` (`Rules.Declutter`, or none
   while the extension is switched off), looks each one up in
   `data/declutter-selectors.json` and replies with the combined selector
   list. If the service can't be reached, the tab keeps hiding whatever it
   already hid. The file has to be read
   there: a content script's fetch of an extension file counts as the web
   page's own request, and Chrome blocks it (rewriting it to
   `chrome-extension://invalid/`, found live 2026-09-28) unless the file is
   declared web-accessible — which would also let any site detect that this
   extension is installed.
2. For each ticked option, every selector listed for it goes into one
   injected `<style>` tag, one `selector { display: none !important }`
   rule each (since 0.12.0: a selector the browser rejects then drops only
   itself, not the whole list), plus any extra CSS the file's `@css` map
   gives the option (the top header needs `--ytd-masthead-height: 0` so the
   page moves up). No `MutationObserver`, no removing nodes. Because it's a live stylesheet rule rather than a one-time DOM
   pass, it keeps hiding matching elements through every future in-app
   navigation on that tab without watching for anything, the same way a
   browser extension's ad-block stylesheet does.
3. Two options do more than hide (0.12.0, the user's list of 2026-11-21):
   - **RedirectHomeToSubscriptions** sends `www.youtube.com/` to
     `/feed/subscriptions`: a click on the logo or Home is caught before
     YouTube handles it, and a check on load, on `yt-navigate-finish` and
     every second catches the rest (back/forward). Only on youtube.com
     itself, never music.youtube.com.
   - **Autoplay** ("Disable autoplay") clicks the player's autoplay switch
     off whenever it finds it on (checked every second; clicking works
     while the switch is hidden), and hides the switch and the "Up next"
     countdown so it stays off. YouTube remembers the switch, so it stays
     off after the option is switched off too, until the viewer turns it
     back on.

   Otherwise nothing here touches navigation: the search box, the address
   bar, typing in `/feed/subscriptions` directly, opening a playlist or
   clicking a video from search all still work. Declutter only changes what's *visible* on a
   page you're already on — an opened video is still classified and
   enforced exactly as described above, completely independently of any
   ticked Declutter option.
4. Shorts are blocked during every lock either way, by the service's
   browser lockdown (a `youtube.com/shorts` blocklist entry). That isn't a
   Declutter option and isn't affected by any of them.

## Local setup

1. **Install** with `../installer/Install.cmd`. That installs and starts the
   service, and installs and registers the native host for Chrome, Edge and
   Brave, for every user (HKLM). The extension shows "not connected" until
   the service is running, and "Companion app not detected" if the app
   isn't installed at all.
2. **Chrome, Edge and Brave: nothing to load.** The service force-installs
   the store copies (`ExtensionInstallForcelist`, see
   `../service/README.md`), in every profile, so the extension installs by
   itself and has no Remove or off switch on the extensions page. Restart
   the browsers if it doesn't appear within a minute or two. Remove any
   unpacked copy you loaded earlier (ID `dkbfaenfbikncbcgecpekmpgcemijgob`),
   or both copies would run.
3. **Quick test of a change (optional).** Load this folder unpacked in a
   throwaway browser profile (Developer mode → "Load unpacked"). In a
   profile that also gets the store copy, both run.

**Updating it:** every browser only gets a change as a store update: raise
the version in `manifest.json`, rebuild
`../store/smart-study-extension-<version>.zip` (without the `key`), and
upload it in Partner Center (Edge) and the Chrome Web Store dashboard.
Once each passes review, the browsers update themselves within a few hours.

Before the installer existed (Milestones 3–8), the host was built and
registered per user from `native-host/install-host.ps1`, pointing at the
repo's own build (`uninstall-host.ps1` removes that). The installer removes
that per-user registration, since it would take priority over the installed
one. Those scripts are only for working on the host itself now.
- Run them yourself, in a PowerShell window opened from the Start menu:
  registrations made from Claude's tools stay inside its sandbox, and your
  browsers never see them.

## Trying it

- Click the extension's toolbar icon. "Service" should say **connected**,
  with the current lock status. If it says "not connected", the service
  isn't running or the host isn't registered.
- Open a YouTube video, then open the page's DevTools Console (F12). You
  should see a line like `[Study Blocker] video aircAruvnKk: "But what is a
  neural network? …" by 3Blue1Brown`, and a new one each time you click to
  another video. Click the line to expand it: underneath are the channel
  id, length, keywords and description. Red errors mentioning
  `reading 'block'` come from Cold Turkey's extension, not this one.
- The verdict itself is logged separately, by `background.js`, which runs
  in the extension's own service worker rather than the page — open its
  console from `chrome://extensions` → Smart Study Distraction Blocker →
  **service worker** (Inspect). You should see a
  `verdict for aircAruvnKk (...): Study [rules] — score 4 (...)` line for
  the same video — `Source` is `channel-rule`, `education-only`, `cache` or
  `rules`.
- Open a site from the app's blocked list: after a blink of the browser's
  own block page, the extension's page should show the logo, "Blocked by
  Smart Study Distraction Blocker" and the site. Back should return to the
  page you came from.
- Enforcement works with or without a lock:
  - Open a video the rules would classify as `Distraction` (e.g. a title
    with "prank" or "reaction" in it): a full-screen **block screen** should
    cover the player, with the logo and "Blocked by Smart Study Distraction
    Blocker". Pressing `k` or space shouldn't start it playing underneath.
  - A title with study words like "lecture" or "tutorial" should keep
    playing.
  - A more ambiguous title, or one with no strong signals either way, should
    show the **grey-zone popup** instead, with "Allow channel" / "Block
    channel" buttons and the channel's name.
- Click **Block channel** on a grey-zone popup: it should turn into the block
  screen and stay paused. The channel should now be in the app's list as
  "Blocked".
- On any video, click the toolbar icon, then **Block channel**: the popup
  should say "Blocked", the video should get the block screen at once, and
  the app should list the channel as "Blocked" within a couple of seconds.
- To see what a lock adds, start one from the desktop app (Lock → 2h, or
  Custom; see `../app/README.md`), or a shorter one with
  `sbctl lock 2` (see `../service/README.md`).
  - A lock turns DevTools off in every browser, which also stops an
    unpacked copy of the extension (see `../service/README.md`).
  - A blocked channel has no remove button in the app until the lock ends.
  - **Allow channel** on a popup still plays the video, and the app lists
    that channel at the top as "Allowed during this lock", with a Block
    button.
- To see Declutter, switch on "Hide comments" and "Hide home feed" under
  "Hide on YouTube" in the app, then load or reload a YouTube tab.
  - The comments section on a watch page and the video grid on the home page
    (leaving just the masthead and search) should both be gone.
  - The toolbar popup's note line should read "Declutter: 2 active."
  - A tab already open when you ticked them catches up when you click back
    into it.

## Not done here (later milestones)

- The Declutter selectors are bundled inside the extension package
  (`data/declutter-selectors.json`), not served by the service. Editing the
  file and reloading is enough for an unpacked copy, but the store copies
  only get a fix through a new store release. See
  `../service/README.md`'s "Not done yet".
- A rule changed in the app reaches an open video when you come back to its
  tab, not while you stay on it (see "How a video gets classified").
