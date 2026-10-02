# app — the desktop app (Milestones 7–9)

**Smart Study Distraction Blocker** (renamed from "Smart Study Blocker" on
2026-09-28, at the user's request): the one window where every setting lives
(plan §6/§12), kept deliberately simple.
- It only sends requests to the service, over the same named pipe `sbctl`
  and the extension's native host use.
- The service applies each request or refuses it, and the window redraws
  from what the service reports.
- It enforces nothing itself, so closing the window changes nothing.

It's WPF on .NET 10, using Windows 11's own look, light or dark to match
Windows.

## Running it

Installed by `../installer/Install.cmd`, it opens from the Start menu or the
desktop shortcut, as `SmartStudyDistractionBlocker.exe`, without asking for
any rights. The installed service is already running by then: it starts with
Windows.

While developing, it can still run straight from the repo, from any
PowerShell window (no admin needed):
```powershell
dotnet run --project D:\Student-Study-Blocker\app\StudyBlocker.App
```

## What's in the window

- **The logo and name**, top left. The logo is the emblem from the repo's
  `logo11.png`, without the words under it (unreadable that small, and the
  name is right beside it).
  - In the window it has no background of its own: `Assets/logo-mark.png` is
    only its shape. It's drawn in the theme's text colour, dark on light and
    white on dark, straight on the window's background.
  - `Assets/logo.ico` is the program's icon (Start menu, desktop, taskbar,
    title bar), at 16 to 256 px. There it keeps a black rounded square, so it
    shows up on any taskbar or wallpaper.
- **Status**, under the name: "Not locked" or "Locked · 5h 12m left". If the
  service isn't running, or is a different version from the app (run the
  installer again), the window says so, greys out and reconnects by itself.
- **Lock**, the blue button: click it to show the lengths, 2h / 8h / 24h /
  72h or **Custom**, and click it again to hide them. It always asks first.
  During a lock it's **Extend**, and the same lengths extend it instead;
  nothing shortens it.
  - Custom is a slider from 1 hour to 7 days (the user's request,
    2026-09-30; it was a text box). It goes hour by hour up to a day, then
    every 6 hours, so the short lengths, where an hour matters, get half
    the slider. The length picked shows above it.
  - While extending, the slider stops at the longest extension that keeps
    the time left within 7 days.
- **Extension on / off**, the switch next to it: switches everything off
  or on (the user's request, 2026-09-28).
  - Off, YouTube isn't filtered or decluttered: block screens and popups
    lift, nothing is hidden, and every video plays.
  - Off, no sites are blocked either. Until 2026-10-01 they stayed blocked
    (they're browser policy, not the extension's); the user counted that as
    a bug. The list is kept, and switching back on blocks them again. Open
    browsers pick the change up within a couple of seconds (below).
  - During a lock the switch is held on, faded. Starting a lock switches it
    back on, and it stays on after the lock until you switch it off.
  - A YouTube tab that's already open picks up the change as soon as you
    click back into it (see `../extension/README.md`).
- **Blocked sites**: type or paste a site and press Add (or Enter).
  - Sites are blocked in Chrome, Edge and Brave all the time, subdomains
    included. Open browsers pick up a change within a couple of seconds
    (see "Pushing changes to open browsers" below).
  - A pasted address keeps only its site (`https://www.reddit.com/r/x`
    becomes `reddit.com`), while a typed path such as `youtube.com/shorts`
    is kept.
- **YouTube channels**: type a channel's name as it appears under its
  videos, then **Allow** or **Block**. Enter blocks. Like blocked sites,
  this applies all the time, not just during a lock. The extension's
  **Block channel** button (in its toolbar popup, over a YouTube video)
  adds to the same list.
  - An allowed channel is always allowed, no matter what: it beats
    education-only mode and the classifier.
  - Your answers to the grey-zone popup on YouTube show up in the same list.
- **Education-only mode**: a video only plays if its title or tags contain
  a study word (see `../service/README.md`).
- **Hide on YouTube**: the 8 Declutter options.

During a lock the window only offers what the service would accept, and the
service refuses the rest regardless:
- Sites and blocked channels lose their remove buttons.
- The Allow button disappears.
- Ticked boxes can't be unticked, and ticking a new one asks first.
- Channels allowed through the popup during this lock sit at the top as
  "Allowed during this lock", with a Block button.

## Breaking a lock early (Milestone 8)

- During a lock, **Break lock early** sits at the bottom of the window. It
  asks first; then the lock stays fully on for 2 hours (plan §10), and
  ends. The service sets the delay, and the app shows whatever it reports.
- Meanwhile a yellow bar at the top shows the countdown, with **Cancel lock
  break**, which doesn't ask. The Extend button is hidden until the break
  is cancelled or the lock ends.
- The button only appears while breaking would end the lock sooner than
  its own time.
- See `../service/README.md` for how the countdown is timed.

## Pushing changes to open browsers

The service writes blocked sites and a lock's lockdown as browser policy.
Chrome, Edge and Brave re-read that on their own only when they start and
every 15 minutes, and on a home PC Windows' Group Policy refresh doesn't
prompt them (tested 2026-09-28).
- So whenever the window sees the policies change (its own edits, a lock
  starting or ending, `sbctl`), it runs `<browser> --refresh-platform-policy`
  for each of those browsers that's already open. The running copy reloads
  its policies at once, without opening a window.
- A browser that isn't running is never started this way: launched cold,
  that switch starts a whole hidden browser.
- "Running" is checked the way the browser checks it itself, by a hidden
  window named after its profile folder.
- Changes made while this window is closed reach open browsers within 15
  minutes, or as soon as it's opened again.

## Files

- `MainWindow.xaml` / `.xaml.cs`: the window and everything it does.
- `ServiceClient.cs`: the requests, one per service call.
- `BrowserRefresh.cs`: pushes policy changes to open browsers (above).
- `SiteName.cs`: tidies a typed or pasted site into a blocklist entry.
- `App.xaml`: the Windows 11 theme, a few shared text styles and the on/off
  switch's look (WPF has no toggle switch of its own).
- `Assets/`: the logo and the program icon (above).

## DevTools during a lock (temporary)

`KeepDevToolsWhileExtensionIsUnpacked` in `MainWindow.xaml.cs` makes every
lock leave DevTools available, the same as `sbctl lock --dev-keep-devtools`.
Otherwise the lock's DevTools policy also switches off Developer mode, which
disables the unpacked extension for the whole lock. Set it to `false` once
the extension is installed from a store (Milestone 0 Phase B / 9).

## Not done yet

- The app doesn't start with Windows, and has no tray icon, deliberately: the
  service enforces everything whether or not the window is open. The one
  thing only the open window does is push policy changes to open browsers
  (above).
