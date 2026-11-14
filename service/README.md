# service — the SYSTEM service (Milestones 1–8)

A .NET Worker Service meant to run as the SYSTEM account. It holds the only
copy of lock state that matters, and it's the only thing that writes
browser policy (plan §17). The desktop app (`../app/`), `sbctl` and the
browser extension only send it requests.

## Layout

- `src/StudyBlocker.Common/`: types shared with clients (`RuleSet`,
  `LockState`, `ServiceStatus`, IPC messages) and `IpcClient`, which the
  desktop app, `sbctl` and the native host all use.
- `src/StudyBlocker.NativeHost/`: the native-messaging bridge the browser
  extension uses to reach the service. Reads one framed message from the
  browser over stdio, relays it to the pipe via `IpcClient`, writes the
  reply back. Registered per-user by `../extension/native-host/install-host.ps1`.
- `src/StudyBlocker.Service/`: the service.
  - `ProtectedStore.cs`: JSON files under `C:\ProgramData\SmartStudyBlocker\`,
    ACL'd to SYSTEM and Administrators only. A Standard account can't read
    or change them.
  - `TrustedClock.cs` + `NetworkTimeSource.cs`: lock timing never reads
    the system clock (plan §4, §17). Real time comes from the `Date`
    header of HTTPS responses and is carried forward on the monotonic
    counter, which clock changes can't touch.
  - `LockStateStore.cs`: lock state and the ratchet. Operations are
    `StartLock`, `ExtendLock`, `UpdateRules` (refuses anything looser while
    locked), `BreakLock`/`CancelBreak` and `Advance`. Nothing shortens a
    lock; the only early end is a "Break lock early" countdown (below).
  - `Policies/`: the browser policy writer and watchdog (below).
  - `Classification/`: the classifier and the channel lists (below).
  - `Ipc/PipeServer.cs`: named pipe `SmartStudyBlocker.Control`. Any
    signed-in user can connect; the server learns each caller's real
    Windows identity. One JSON line in, one out.
  - `Worker.cs`: every 5 s counts lock time and re-checks the browser
    policies; syncs network time at startup and every 30 min.
  - `LinkStore.cs` + `LinkService.cs`: the link to LockIn (see "The link to
    LockIn" below). `LockStateStore` holds the session it starts.
  - `Program.cs`: runs as a Windows service (see "Running it" below), one
    copy at a time.
- `tests/StudyBlocker.Service.Tests/`: 138 unit tests for the ratchet, the
  clock, the policy writer, the classifier and the LockIn link. They pin
  down plan §17's invariants.
- `tools/StudyBlocker.Cli/`: `sbctl`, a developer command line that sends
  the same requests the app does, for testing without the app or a browser.
- `StudyBlocker.slnx` also includes the desktop app,
  `../app/StudyBlocker.App`.

## How lock time is counted

A lock stores its duration and the time counted so far, which only grows.
Uptime always counts (including sleep). If the lock started while the
clock was network-synced, then whenever it's synced again the real elapsed
time is used instead, so time the PC was switched off counts too. Time that
can't be verified doesn't count: being offline can make a lock last
longer, never shorter.

## Breaking a lock early (Milestone 8)

Plan §10's one way out of a lock before its time, slow on purpose.

- `break-lock` (the app's **Break lock early**) starts a countdown of
  `LockStateStore.BreakDelay`. The lock stays fully on meanwhile: the
  ratchet and the browser lockdown all still apply. When the countdown
  reaches zero, the lock ends the same way as when its time is up.
- **The delay is 2 hours** (plan §10). It was 4 minutes while the user
  tried it out, then set to 2 hours at their request (2026-09-28).
- `cancel-break` (the app's **Cancel lock break**; plan §10 words it
  "Cancel, I'm back") stops the countdown and the lock carries on. A later
  break starts the whole countdown again, so partial countdowns can't be
  saved up.
- The countdown is counted like the lock itself (above): uptime always
  counts, and if it started on network time, so does time the PC was off.
  Time from before the break was asked for never counts toward it, even
  when a network sync makes the lock's own time jump ahead.
- It's refused with no lock, or when the lock would end sooner anyway.
  Asking again while a countdown runs changes nothing, and so does
  cancelling when none is running.
- Extending the lock doesn't stop a countdown; only cancelling does. The
  app hides its extend buttons during one.
- Lock time is counted every 5 s (it was 30 s before Milestone 8), so a
  lock or a countdown ends within a few seconds of its time.

## The extension switch

`RuleSet.ExtensionEnabled`, the app's "Extension on / off" switch (added at
the user's request, 2026-09-28).
- Off, `classify-video` judges nothing and caches nothing: it answers every
  video with Study, source `switched-off`, so the video plays and anything
  the extension was showing lifts. The extension also stops hiding
  Declutter surfaces (see `../extension/README.md`).
- Off, the blocked sites are lifted too (the user's request, 2026-10-01:
  off means nothing is blocked). `BrowserPolicies.For` leaves out
  `URLBlocklist`, so the policy writer restores whatever was there before,
  and the list itself stays saved for when it's switched back on. The
  browser lockdown only exists during a lock, when the switch is always on.
- It rides the rules ratchet: during a lock it can be switched on, never
  off. `StartLock` switches it back on, since a lock is for enforcing, so
  it's always on during a lock. Plan §17's "never a silent allow during a
  lock" still holds.
- It defaults to on, which is also what rules saved before it existed load
  as.
- `sbctl rules --extension-off` switches it off.

## Browser policies (Milestone 2, reworked in 7)

These become Chromium policies under
`HKLM\SOFTWARE\Policies\{Google\Chrome, Microsoft\Edge, BraveSoftware\Brave}`,
using each browser's own names (verified in Milestone 0):

| What | Policy | When |
|---|---|---|
| blocked sites | `URLBlocklist` list | always, unless the extension switch is off |
| the extension, in every browser | `ExtensionInstallForcelist` entry for its store copy (Edge Add-ons in Edge, the Chrome Web Store in Chrome and Brave) | always |
| Shorts | `URLBlocklist` entry `youtube.com/shorts` | during a lock |
| no Incognito | `IncognitoModeAvailability` = 1 (Edge: `InPrivateModeAvailability`; Brave also `TorDisabled` = 1) | during a lock |
| no Guest | `BrowserGuestModeEnabled` = 0 | during a lock |
| no DevTools | `DeveloperToolsAvailability` = 2 | during a lock |
| no secure DNS | `DnsOverHttpsMode` = `off` | during a lock |

- **Blocked sites apply all the time**, locked or not (plan §10). A lock
  only stops the list shrinking. A site blocks its subdomains too
  (`reddit.com` covers `old.reddit.com`), and a path blocks just that part
  of a site (`youtube.com/shorts`).
- **Blocked apps apply like blocked sites** (2026-11-09): `RuleSet.BlockedApps`
  rides the same ratchet, lapses with the extension switch off and on a
  LockIn session-break (`LockStateStore.Enforced`), and a LockIn session
  neither adds nor removes any. Two halves:
  - *Launch*: `AppBlockPolicies` gives each blocked program an Image File
    Execution Options `Debugger` value (`HKLM\SOFTWARE\Microsoft\Windows
    NT\CurrentVersion\Image File Execution Options\<exe>`) pointing at the
    installed app with `--app-blocked`, so Windows opens the notice instead.
    `PolicyWriter` owns these like any policy: ledgered, repaired within a
    tick, restored on release and by `--restore-browser-policies`. A
    development run (no `..pp\SmartStudyDistractionBlocker.exe` next to
    the service) writes none.
  - *Running*: `AppWatcher` sweeps the process list every second and ends
    any copy of a blocked program in a user session, by file name or by the
    name its version info says it was built under (catches a renamed copy).
    It then opens the notice on that desktop (`UserSessionNotice`, at most
    once a minute per program). This catches Store apps, which ignore the
    Debugger value, and programs already open when a block begins.
  - `BlockedApp.Protected` (Windows' shell and core processes, this blocker,
    LockIn) can never be blocked; `RuleRequests.Valid` refuses them.
  - LockIn's `override-rules` doesn't send `BlockedApps` (or
    `BlockAdultContent`); `RuleRequests.KeepUnsent` keeps their current
    values when a field is missing, so the link's wire format is unchanged.
- **The browser lockdown comes and goes with the lock.** Everything else
  in the table is switched on when a lock starts and put back when it
  ends. `BrowserPolicies.For` derives it from `LockState.IsLocked`; it's
  not stored in the user's `RuleSet`. Until Milestone 7 these were rules
  that stayed on after a lock until changed with `sbctl`. The app has no
  switches for them, so they're now tied to the lock itself. To block
  Shorts all the time, add `youtube.com/shorts` as a blocked site.
- **The extension is force-installed in Chrome, Edge and Brave**, all the
  time and for every profile and user on the PC: plan §7's "force-install
  the extension and prevent its removal". Edge gets the Edge Add-ons copy
  (approved 2026-10-04); Chrome and Brave get the Chrome Web Store copy
  (unlisted, ID `fgflnbhgcppnodpggmbeomdpciebgaln`). The browser installs
  it by itself, with no Remove or off switch on its extensions page, and
  the app's switch makes it idle instead.
  - On a PC that isn't joined to a domain, Chrome and Edge only
    force-install extensions from their own store, which is why both
    had to be published. Edge's policy also applies to profiles signed in
    with a Microsoft account.
  - A browser uninstalls a force-installed extension once it leaves the
    list, so the uninstaller is the only thing that takes it off
    (`--restore-browser-policies`, below).
  - Brave takes the Chrome Web Store entry. Its support for the policy
    wasn't confirmed before the first install (`../policy-test/README.md`,
    finding 2), so check `brave://extensions` afterwards.
  - A store copy doesn't need Developer mode, so every lock turns DevTools
    off in all three browsers. An unpacked copy of the extension stops
    running during a lock; the old `--dev-keep-devtools` exception for it
    is gone.
- **Open browsers don't see a change by themselves.** Chrome, Edge and
  Brave re-read their registry policies only when they start, every 15
  minutes, or on a Windows Group Policy notification. Found live
  2026-09-28: an open Edge kept blocking a removed site, and not blocking a
  newly added one, until it was restarted.
  - On this home PC, neither `RefreshPolicyEx` nor `gpupdate /force`
    prompted them (tested outside the tool sandbox).
  - What works is Chromium's own `--refresh-platform-policy` switch, handed
    to the copy that's already running. It has to run in the user's session,
    which a SYSTEM service isn't, so the desktop app does it after every
    change it sees (`../app/StudyBlocker.App/BrowserRefresh.cs`).
  - A change made while the app is closed (a lock ending on its own, or
    `sbctl`) reaches open browsers within 15 minutes, or as soon as the app
    is opened.
- **Nothing is lost.** Before taking over a setting, the service records
  what was there in `policy-ledger.json` in the protected store. When a
  rule is turned off or a lock ends, the setting goes back to exactly that,
  including entries some other software had in the blocklist.
- **Watchdog:** every 5 seconds the service compares the registry with
  what it should be and rewrites anything that differs, logging it as a
  warning.

## Classifier (Milestone 4, extended in 7)

Plan §8's Basic tier: study vs. distraction, cheap signals first, cached so
each video is judged once. `Classification/ClassifierStore.cs` runs the
whole pipeline, in priority order:

1. **Your channel lists** (set from the app, by answering the extension's
   grey-zone popup, with the extension's **Block channel** button, or with
   `sbctl channel`). They always win, and they're
   re-checked live on every call rather than cached, so a change affects
   the very next video. An **Allowed** channel is the "always allowed, no
   matter what" list: it beats education-only mode and even instant-block
   words. **Blocked** channels are always a distraction.
   - A channel saved from the popup is matched by its YouTube channel id,
     so a copycat channel with the same name doesn't share the decision.
   - A name typed into the app matches the channel name shown under a
     video, ignoring case.
   - Typing a name that's already listed changes that entry instead of
     adding a contradictory second one.
   - For the same reason, blocking by id with the channel's name (the
     extension's Block channel button) also blocks an entry typed with that
     name, so a channel allowed by name doesn't end up on both lists.
   - Entries saved before Milestone 7 only had an id; each gets its name
     the next time a video from that channel is seen.
2. **Education-only mode** (Milestone 7, `RuleSet.EducationOnly`): a
   video with no study word in its title or tags is a distraction straight
   away.
   - The study words are `RuleScoring`'s own list ("lecture", "tutorial",
     "course", "chapter", "exam", "syllabus", …), matched as whole words
     with an optional plural, so "example" doesn't count as "exam".
   - The description isn't read: "of course" would count as "course".
   - A study word is required, not a free pass: the video still goes
     through the steps below, so "Lecture hall prank" is still blocked.
   - This step is never cached, so switching the mode off gives every video
     its ordinary verdict back.
3. **The verdict cache**, keyed by video id.
4. **`RuleScoring.cs`**: the "lightweight CPU model" from plan §14. It's a
   small, explainable weighted scorer over the title, description and
   tags, not a trained model. An actual local model is the separate, opt-in
   "Local AI" tier the plan defers past v1.
   - **YouTube's category** is a hint (plan §8: "category is a hint, not a
     verdict"), and not a symmetric one:
     - A Study category (Education, Science & Technology, Howto & Style) is
       worth only +1, never enough by itself to be confident, since the
       plan specifically warns about content miscategorized *outside*
       Education.
     - A Distraction category (Entertainment, Comedy, Gaming, Film &
       Animation, Music) is worth -2, enough alone to cross the Distraction
       threshold. This was tightened from a symmetric ±1 on 2026-09-28,
       after live testing showed Gaming videos with no other signal
       sitting in the grey zone every time. A real study keyword can still
       outweigh it.
     - Music follows plan §8's literal wording: "Music counts as
       distraction unless a specific channel is allowed via the popup."
   - **Instant-block keywords** (currently just "prank") skip scoring
     entirely and always return Distraction. The user's own words, the
     same day: "must be instant blocks".
   - **Podcasts and similar infotainment** are routed straight to the grey
     zone regardless of score, matching the plan's own examples of it.
   - **Learned channels:** a channel whose videos have scored consistently
     one way (at least 5 samples, ≥80% agreement) nudges its next
     borderline video. Plan §8: "learn channels that are consistently one
     way".
5. Below the study/distraction thresholds, the result is **Grey**, which the
   extension shows as an in-page popup (Milestone 5). Per plan §17,
   uncertainty during a lock is never treated as a silent allow.

Only Basic is implemented. The plan's other three engine options (Local AI,
BYOK, hosted) don't exist yet, and the app doesn't offer a choice until one
does.

## Channel lists during a lock (Milestones 5 and 7)

Outside a lock the lists are freely editable in either direction (plan
§10). During a lock, `ClassifierStore.SetChannelRule` enforces the ratchet
(plan §2/§17: nothing can be unblocked until the timer ends):

- **Blocking always works**: by name in the app, from the popup, from the
  extension's Block channel button, or from the app's "Allowed during this
  lock" row.
- **Taking a channel off the allowed list always works**, since the channel
  then goes back to being judged by the rules, which is stricter.
- **A blocked channel stays blocked** until the lock ends, however and
  whenever it was blocked. Up to Milestone 6, a channel blocked *before*
  the lock could still be cleared during it.
- **A new allowed channel can only come from the grey-zone popup** (plan
  §9), and only for a video the classifier itself judged Grey, from that
  same channel. The extension sends the video id, and the service checks
  it against its own cached verdict. Typing a name into the app's Allow
  box is refused, or one entry would switch the blocker off for that
  channel mid-lock. Verdicts cached before Milestone 7 get their channel
  filled in the next time they're read, so old grey videos still work.
- The popup's allows during the current lock are plan §9/§11's **"Allowed
  during this lock"** list. `list-channel-rules` flags them
  `AllowedDuringCurrentLock`, and the app shows them at the top of the
  channel list with a Block button. This tracking is persisted
  (`classifier-state.json`), so it survives a service restart mid-lock.

The verdicts themselves are enforced the same way whether or not a lock is
on (plan §10: outside a lock "the same rules still apply"): the extension
blocks a Distraction and a blocked channel's videos all the time. See
`../extension/README.md`. A lock only changes what can be edited.

## YouTube Declutter (Milestone 6)

Plan §9's checklist of "algorithmic/temptation surfaces" to hide: home
feed, recommended videos, end-screen suggestions, autoplay, comments,
trending/explore, live chat, subscriptions feed, and (2026-11-09) the
playlist panel beside a video watched from a playlist (`Playlists`).

- `RuleSet.Declutter` is a plain list of `DeclutterOption` values, next
  to `BlockedDomains`. It's the same shape of thing (a profile setting
  that can only grow during a lock), so it rides the existing
  `IsAtLeastAsStrictAs` ratchet: `update-rules` refuses to drop an entry
  while locked. It's set from the app's "Hide on YouTube" boxes, or with
  `sbctl rules --declutter-comments ...`.
- The service only tracks which options are ticked; it has no opinion on
  *how* to hide anything. The DOM selectors for each option live entirely
  in `../extension/data/declutter-selectors.json`, read only by the
  extension. When YouTube redesigns something, only that JSON file needs
  fixing, never this service.
- Per plan §9, this only hides feed and recommendation surfaces; it never
  blocks navigation. Search, subscriptions, a playlist's own page and any video opened
  directly keep working. An opened video is still classified and enforced
  as usual; Declutter only changes what's visible *around* it. Shorts are
  blocked during every lock regardless, by the lockdown's
  `youtube.com/shorts` policy entry.

## The link to LockIn

LockIn is a separate study app. This service works exactly as before
without it; once the two are linked (the user's request, 2026-10-24):
- LockIn won't start a focus session unless this service is running with
  the extension on, and switches the extension on itself if it was off.
- While a LockIn session runs, the rules are held at their strictest, and
  when it ends they go back to how they were.
- Outside sessions, LockIn can override a lock: switch the extension off or
  end the lock, even though the app can't.

**Pairing** (`LinkStore.cs`).
- While nothing is linked, the service leaves a one-time offer, a random
  token in `C:\Program Files\Smart Study Distraction Blocker\link\pairing-offer.json`.
  Every user can read that folder; only administrators and the service can
  write it. Only the token's hash is kept in the protected store. If the
  file is deleted or garbled, the next tick puts a new one there. The `link`
  folder itself is the installer's: without it there is quietly nothing to
  offer (a copy run from the repo, or the first 0.3.2 setup, which installed
  into Program Files (x86) instead).
- LockIn sends the token in `pair`. The service deletes the offer, notes
  which Windows account asked, and answers with a long-lived secret, once.
  Only its hash is kept (`link.json`).
- Every later link request carries that secret (`IpcRequest.Secret`) and has
  to come from that same Windows account. Refusals are `link:not-linked`,
  `link:bad-secret`, `link:wrong-user`, `link:bad-offer` and
  `link:already-linked`.
- `unpair` forgets the link and leaves a fresh offer. It needs the secret;
  if LockIn has lost it, the app's reload arrow (`reset-link`) does the same
  with no secret, except during a LockIn session, and so does the installer
  with `-ResetLink` (double-click `../installer/Reset-LockIn-Link.cmd`).
- The secret lives in LockIn's profile, which the daily account can read.
  So this keeps other programs and impulses out; it is not a wall against
  someone determined and technical. That is the intent: using the link
  needs no administrator rights.

**The session lock** (`LockStateStore`, `linked-session.json`).
- `session-start` switches the extension on if it was off (and remembers
  that), then holds the ratchet as a lock does: `update-rules`
  can only get stricter, `break-lock` is refused, and so are the channel
  lists' loosening. Every refusal is the one sentence the app shows in its
  banner: "Locked from LockIn, end LockIn session to disable". It wins over
  a lock's own message when both apply. A lock can still be started or
  extended meanwhile.
- It has no timer of its own, only a **lease**: LockIn sends
  `session-heartbeat` at least every 20 s, and 90 s without one ends the
  session. A **cap** (the session's planned length plus 3 hours, at most 12)
  ends it even if heartbeats never stop. Both are counted on the monotonic
  counter, like lock time.
- A gap of more than 30 s between two counts means the service itself was
  asleep or stalled, not LockIn, so LockIn gets a fresh lease.
- The session survives a service restart (the lease starts again), so an
  update installed mid-session doesn't drop it.
- Ending it, by `session-end`, the lease or the cap, puts the extension
  back **off if it was off before** and LockIn switched it on. Never under a
  lock: one started meanwhile keeps the extension on.
- `session-start` again while one runs only renews the lease, switches the
  extension back on if something did and can raise the cap, so it never
  forgets what the extension was before the session.

**Override** (`override-rules`, `override-end-lock`). The ratchet doesn't
apply: LockIn is what the user chose to trust when they linked it. Refused
during a LockIn session, which is exactly when the user asked for no way
out. `override-end-lock` ends a lock at once, with no countdown.

**Wire format** (`StudyBlocker.Common/LinkMessages.cs`): PascalCase
property names, matched case-sensitively. LockIn depends on these names and
strings as they are.

## Requests

| Request | Payload | Notes |
|---|---|---|
| `ping` | | |
| `get-status` | | lock state (incl. the rules and any break countdown), trusted time, system time, `BreakDelayMs`, `Link` (`IsLinked`, `OfferAvailable`, `SessionActive`) |
| `update-rules` | `RuleSet` | the whole rule set; refused if looser during a lock |
| `start-lock` | `{"Minutes": n}` | up to 7 days; refused if already locked |
| `extend-lock` | `{"Minutes": n}` | while locked; at most 7 days remaining |
| `break-lock` | | "Break lock early": the lock ends after `BreakDelayMs`; see above |
| `cancel-break` | | "Cancel lock break" |
| `classify-video` | `VideoMetadata` | returns `{Verdict, Reason, Source}`; applies education-only mode if it's on; Study [`switched-off`] while the extension switch is off |
| `set-channel-rule` | `ChannelRuleRequest` | `ChannelId` or `Name`, `Rule` = `Allow`/`Block`/`Clear`, `VideoId` from the popup; during a lock see "Channel lists during a lock" |
| `list-channel-rules` | | every channel entry with its name, each flagged `AllowedDuringCurrentLock` |
| `pair` | `{"Offer": "..."}` | links LockIn and answers `{"Secret": "..."}`, once. Needs the offer, not a secret |
| `unpair` | | needs `Secret`; refused during a LockIn session |
| `reset-link` | | the app's reload arrow: forgets the link with no secret and leaves a fresh offer; refused during a LockIn session |
| `session-start` | `{"MaxMinutes": n}` | needs `Secret`; 1 to 720; see "The link to LockIn" |
| `session-heartbeat` | | needs `Secret`; replies with the status, whose `Link.SessionActive` says whether the session is still on |
| `session-end` | | needs `Secret`; harmless with no session |
| `override-rules` | `RuleSet` | needs `Secret`; applies with no ratchet; refused during a session |
| `override-end-lock` | | needs `Secret`; ends a lock now; refused during a session |

Reads (`ping`, `get-status`, `list-channel-rules`) are logged at Debug
level only, since the app polls them every couple of seconds while it's
open.

## Not done yet

- Blocking youtube.com outright when the extension stops reporting in:
  not built.
- The classifier's word lists (also education-only mode's study words) are
  embedded in `RuleScoring.cs`, not an editable data file. That's fine for
  Basic tier v1, and worth revisiting once the installer (Milestone 9) has
  a real install directory to put one in.
- Local AI / BYOK / hosted engines (plan §8's other three tiers): Basic
  only for now.
- The Declutter selectors live in a file bundled inside the extension
  package, not one the service can push updates to.
  - Every browser now runs a store copy, which can't be updated that way
    without a new store release, which is exactly what plan §9 wants
    avoided.
  - Worth moving to a service-served file (like channel rules) once that
    matters.
- The rest of Milestone 9: the account-setup guide and the Local AI tier
  (see `../installer/README.md`).
- A rule changed while a video is open (a channel blocked in the app,
  education-only or the extension switched on or off, a Declutter box
  ticked) reaches that tab when you click back into it, not while you stay
  on it.
- A channel name typed into the app matches any channel with exactly that
  name, so a copycat channel with the identical name would match too.

## Running it

It's installed as the Windows service `SmartStudyBlockerService` by
`../installer/Install.cmd`, which is also how to update it after a code
change (see `../installer/README.md`). It starts with Windows, as SYSTEM,
and logs to the Windows Application event log (source
`SmartStudyBlockerService`), since it has no console.

- Only one copy runs at a time: a second one (say, the old
  `dotnet run --project ...\StudyBlocker.Service` from an Administrator
  window) says so and exits instead of fighting the installed one over the
  pipe and the browser policies.
- Started without administrator rights, it says so and exits: it can't open
  its protected store.
- `StudyBlocker.Service.exe --restore-browser-policies` puts every browser
  policy the service took over back as it was, then exits. The uninstaller
  uses it after stopping the service. It's refused during a lock.

The desktop app (`../app/README.md`) is the normal way to drive it. `sbctl`
sends the same requests from any shell:

```powershell
dotnet run --project D:\Student-Study-Blocker\service\tools\StudyBlocker.Cli -- status
dotnet run --project D:\Student-Study-Blocker\service\tools\StudyBlocker.Cli -- rules --block-site reddit.com --education-only --declutter-home-feed --declutter-comments
dotnet run --project D:\Student-Study-Blocker\service\tools\StudyBlocker.Cli -- lock 5
dotnet run --project D:\Student-Study-Blocker\service\tools\StudyBlocker.Cli -- break
dotnet run --project D:\Student-Study-Blocker\service\tools\StudyBlocker.Cli -- cancel-break
dotnet run --project D:\Student-Study-Blocker\service\tools\StudyBlocker.Cli -- classify --id v1 --title "Chapter 5: Thermodynamics full lecture" --category Education
dotnet run --project D:\Student-Study-Blocker\service\tools\StudyBlocker.Cli -- channel --name "3Blue1Brown" allow
dotnet run --project D:\Student-Study-Blocker\service\tools\StudyBlocker.Cli -- channels
```

`rules` sets the whole rule set, so during a lock repeat every current
option plus the new one. With no options it clears the rules (outside a
lock only). `classify` stands in for the extension, for testing the
classifier without a browser.

## Tests

```powershell
dotnet test D:\Student-Study-Blocker\service\StudyBlocker.slnx
```

The installed service runs from Program Files, so it no longer holds this
repo's build output open.
