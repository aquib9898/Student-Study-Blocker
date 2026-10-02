# test-extension

Feasibility test, not a v1 component: proves a browser extension can cause a
registry change on Windows. It exists to correct one assumption before real
build work starts — **an extension cannot touch the registry directly.**
MV3 extensions run in a sandbox with no filesystem/registry API. The only
path from extension code to the OS is **native messaging**: the extension
sends a message to a registered native host process, and that separate,
native process touches the registry.

This mirrors the real architecture in the plan (Section 14: "native
messaging between extension and service").

## What this does and does not prove

- **Does prove:** extension -> native messaging -> native host -> registry
  write/read, working end to end, across Chrome/Edge/Brave.
- **Does not prove:** real policy enforcement (force-install with no remove
  button, URL blocklists, disabling Incognito/DevTools). That's a separate,
  bigger test — the plan's own "Milestone 0" — and needs the extension
  published unlisted plus `HKLM\Software\Policies\...` policy keys, which
  need admin rights. This test deliberately writes to a harmless custom key
  under `HKCU\Software\SmartStudyBlockerTest` instead, so it needs no admin
  rights and can't break anything.

## Layout

```
test-extension/
  extension/               MV3 extension (popup with Write/Read buttons)
  native-host/
    host.js                 Native messaging host (Node) - does the reg.exe calls
    host.bat                Wrapper so Chrome can launch host.js as an exe
    com.smartstudyblocker.test.json   Native host manifest
    install.ps1              Registers the host for Chrome/Edge/Brave (HKCU, no admin)
    set-extension-id.ps1     Fills in the real extension ID after you load it
    uninstall.ps1            Removes everything this test created
```

Already verified standalone: `host.js` correctly writes and reads back a
value via `reg.exe` (ran outside the browser as a sanity check before
wiring up native messaging, which is the fiddlier part to debug).

## Setup

1. **Register the native host** (PowerShell, no admin needed):
   ```powershell
   cd D:\Student-Study-Blocker\test-extension\native-host
   .\install.ps1
   ```

2. **Load the unpacked extension** in your browser:
   - Chrome: `chrome://extensions`
   - Edge: `edge://extensions`
   - Brave: `brave://extensions`
   - Enable "Developer mode", click "Load unpacked", select the
     `test-extension\extension` folder.
   - Copy the extension ID shown on its card.

3. **Wire the ID into the host manifest:**
   ```powershell
   .\set-extension-id.ps1 -ExtensionId <paste-id-here>
   ```

4. **Test it:** click the extension's toolbar icon, click "Write test
   value", then "Read test value". You should see the same timestamp both
   times. You can also confirm independently from a terminal:
   ```powershell
   reg query "HKCU\Software\SmartStudyBlockerTest" /v TestValue
   ```

## If it doesn't connect

- `chrome.runtime.lastError` about "Specified native messaging host not
  found" usually means the registry key from `install.ps1` doesn't match
  the browser you're testing in, or the JSON manifest path is wrong.
- "Access to the specified native messaging host is forbidden" means the
  extension ID in `com.smartstudyblocker.test.json` doesn't match — rerun
  `set-extension-id.ps1`.
- Reload the extension (and possibly the browser) after editing the host
  manifest — browsers cache native host lookups per-session sometimes.

## Shorts guard (`extension/shorts-guard.js`)

The policy test showed `URLBlocklist` blocks `youtube.com/shorts` on real
page loads (typed URL, refresh) but not when you click a Short inside
YouTube, because YouTube swaps content without a real load. This script
turns those in-app switches into real loads so the policy blocks them:

- Plain left-clicks on any `/shorts/` link are stopped and replaced with a
  real navigation to the same URL.
- A URL watcher catches Shorts routes that don't come from a click
  (back/forward, autoplay), and reloads them for real.

It never blocks anything itself; without the policy applied, Shorts just
load normally (no reload loop).

Test with `policy-test\set-policies.ps1` applied: from the YouTube home
page, click a Shorts thumbnail. You should land on the browser's "blocked"
page instead of the Short. Also try: open a normal video, then press Back
after the Short is blocked, and scroll the home page's Shorts shelf.

## Cleanup

```powershell
cd D:\Student-Study-Blocker\test-extension\native-host
.\uninstall.ps1
```
Then remove the unpacked extension from the browser's extensions page.

## Once this passes

The next real step is the plan's Milestone 0: publish a trivial unlisted
extension, write actual Chromium policies
(`HKLM\Software\Policies\Google\Chrome\ExtensionInstallForcelist` etc. —
requires the Admin account), and confirm force-install with no remove
button, across all three browsers and all profiles, on a normal PC. That
test needs admin rights and touches real policy; this one deliberately
doesn't.
