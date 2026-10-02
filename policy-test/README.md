# policy-test — Milestone 0, the real version

> **Superseded for Phase A.** Since Milestone 2 the service writes these
> policies itself (`../service/README.md`). Run `unset-policies.ps1` once
> before running the service so it starts from the keys' original state;
> after that, don't run `set-policies.ps1` while the service is managing
> them. Phase B below still applies.

Tests the actual claims your plan's Milestone 0 depends on, using real
Chrome/Edge/Brave enterprise policy (`HKLM\SOFTWARE\Policies\...`), not the
harmless HKCU sandbox key from `test-extension/`. Two things below came out
of checking primary sources before building this — both matter enough to
change how you sequence the real work.

## Finding 1: force-install-with-no-remove-button needs the extension to actually be on the store

On a Windows PC that is **not** domain-joined (a normal home PC — your
actual target), Chrome and Edge will only silently force-install an
extension with no remove/disable button if that extension is hosted on the
Chrome Web Store / Microsoft Edge Add-ons store. A self-hosted
`update_url` + signed `.crx` — which would otherwise be the fast, free way
to iterate — is rejected in that scenario. Quoting Microsoft's own policy
doc directly:

> "For Windows instances not joined to a Microsoft Active Directory domain,
> forced installation is limited to apps and extensions listed in the
> Microsoft Edge Add-ons website."
> — [Microsoft Edge ExtensionInstallForcelist docs](https://learn.microsoft.com/en-us/deployedge/microsoft-edge-policies/extensioninstallforcelist)

Google states the Chrome equivalent on [their own support page](https://support.google.com/chrome/a/answer/7532015?hl=en):

> "To automatically install an app or extension that's not in the Chrome
> Web Store, computers must be joined to a domain using Microsoft Active
> Directory."

This is exactly why your plan's Milestone 0 already said "publish a
trivial **unlisted** test extension" rather than self-host — that instinct
was correct, and there's no shortcut around it. Domain-joining your home PC
just to unlock self-hosting would be a much bigger, weirder change to your
machine than paying the Chrome Web Store's one-time $5 fee, so: **publish
unlisted, don't self-host.**

## Finding 2: Brave's support for this policy is inconsistently reported

Chrome and Edge share near-identical enterprise policy engines. Brave forks
Chromium but doesn't obviously inherit this cleanly — community reports are
contradictory:

- One [open Brave GitHub issue](https://github.com/brave/brave-browser/issues/37939)
  shows Brave currently reads Chrome's own external-extensions registry
  key (`Software\Google\Chrome\Extensions`), not a Brave-specific one, for
  the *unrelated* older "external extension" install mechanism.
- A [separate issue](https://github.com/brave/brave-browser/issues/27149)
  about `ExtensionInstallForcelist` specifically was closed as
  "works-for-me" with no confirmed working registry path.
- A [Brave community thread](https://community.brave.app/t/does-brave-support-extensioninstallforcelist/587414)
  asking the direct question got no official answer before auto-closing.

**Practical takeaway:** Brave reads policy from
`HKLM\SOFTWARE\Policies\BraveSoftware\Brave` (per Brave's own
[TorDisabled policy PR](https://github.com/brave/brave-core/pull/3692)),
not `...\Brave-Browser`, which is only its install folder. The failure
people report is "the policy shows up in `brave://policy` but the extension
never installs", so on Brave check `brave://extensions` as well as the
policy page. Don't assume "one codebase, three browsers" extends to "one
policy write, three browsers".

## What to test, in order

### Phase A — policies that don't need the store (test these now)

Policy names and values live in `common.ps1`. They aren't identical across
browsers: Edge's name for Chrome's `IncognitoModeAvailability` is
`InPrivateModeAvailability`, and Brave also needs `TorDisabled`, because its
"Private window with Tor" is a separate private-window type with its own
policy.

Edge also skips some policies on profiles signed in with a personal
Microsoft account (since Edge 116). Each policy's Microsoft Learn page has
an "Applies to a profile that is signed in with a Microsoft account"
line. `ExtensionDeveloperModeSettings` says **No**, so on a signed-in Edge
profile only `DeveloperToolsAvailability = 2` locks Developer mode. Any
Edge policy the real lock relies on needs that line to say Yes, or a
passing test on a signed-in profile.

Fully quit all three browsers first (check Task Manager for leftover
`chrome.exe` / `msedge.exe` / `brave.exe` background processes; Brave only
applies `TorDisabled` on restart). Then, from an **Administrator**
PowerShell window:
```powershell
powershell -ExecutionPolicy Bypass -File "D:\Student-Study-Blocker\policy-test\set-policies.ps1"
```
The first run saves each browser's policy key to `backup\` before touching
it; every run rebuilds each key from that saved state plus the test
policies, so it's safe to edit `common.ps1` and re-run. Then open each
browser and check `chrome://policy`, `edge://policy`, `brave://policy` —
every policy should show status **OK**.

Verify each behavior actually changed:
- Incognito/InPrivate window option is gone from the menu (Ctrl+Shift+N does nothing)
- Brave only: "New private window with Tor" is gone from the menu too
- Guest mode option is gone from the profile menu
- DevTools won't open (F12 / Ctrl+Shift+I do nothing)
- `chrome://settings/security` (and Edge/Brave equivalents) shows secure DNS locked off
- `example.com` shows the built-in "blocked by administrator" page
- **`youtube.com/shorts` blocked two ways, not just one:** (a) type a Shorts
  URL directly into the address bar, confirm it's blocked, AND (b) from a
  normal YouTube video page, click a Shorts thumbnail in the feed without
  typing a URL. YouTube is a single-page app — a client-side route change
  doesn't always trigger the kind of navigation the URL-blocklist policy
  intercepts. If (b) slips through, the extension needs to backstop it by
  detecting the in-app route change itself, not just rely on policy.
  **Result (Chrome, Edge, Brave):** (a) blocked, (b) plays the Short. The
  policy only catches real navigations; in-app clicks need the extension.

You don't need the extension published to run any of this.

Read-only check anytime (no admin needed). Lists every value under each
browser's policy key, not just ours, plus any saved backup:
```powershell
powershell -ExecutionPolicy Bypass -File "D:\Student-Study-Blocker\policy-test\check-policy-status.ps1"
```

Revert (Administrator window). Restores each key exactly as it was before
the first run, then deletes the backups:
```powershell
powershell -ExecutionPolicy Bypass -File "D:\Student-Study-Blocker\policy-test\unset-policies.ps1"
```
Parent keys the test had to create (`HKLM\SOFTWARE\Policies\Google`,
`...\BraveSoftware`) are left behind empty; they have no effect.

### Phase B — the force-install test (needs the store)

1. **Chrome Web Store.** Create/use a Google account, register as a
   developer at [developer.chrome.com/docs/webstore/register](https://developer.chrome.com/docs/webstore/register)
   (one-time $5 fee, requires 2-Step Verification on the account) — this is
   an account-creation-and-payment step, so it needs to be you, not me.
   Zip `test-extension/extension/` (once it's built out further — the
   current popup-only version is enough to test the mechanism), upload it
   as a new item, and set visibility to **Unlisted** during submission.

2. **Edge Add-ons.** Separate Microsoft account, register at
   [Partner Center](https://learn.microsoft.com/en-us/microsoft-edge/extensions/publish/create-dev-account),
   upload the same package, mark it **Hidden** (Edge's term for
   unlisted/private) instead of public. Also your step to do.

3. Once each is actually live (review can take anywhere from hours to a
   few days — kick both off in parallel, and do Phase A while you wait),
   write the real forcelist entry:
   ```
   HKLM\SOFTWARE\Policies\Google\Chrome\ExtensionInstallForcelist\1
     = "<your-real-extension-id>;https://clients2.google.com/service/update2/crx"
   ```
   (Edge's default update URL is `https://edge.microsoft.com/extensionwebstorebase/v1/crx`
   instead — same pattern.) I'll write the actual apply/revert scripts for
   this once you have real extension IDs, since they don't exist yet.

4. Confirm on **all three browsers**, and on **more than one Chrome/Edge
   profile** on this machine (add a second profile and check it there too
   — the plan's "all profiles" claim in Milestone 0 is itself untested):
   - Extension appears already installed, enabled, with no remove/disable
     button, no way to turn it off from `chrome://extensions`.
   - Restarting the browser doesn't clear it.
   - Setting YouTube to "block all extensions on this site" (per-site
     extension setting, stored as `extensions.user_permissions.restricted_sites`
     in the profile's `Preferences`) doesn't stop it. That switch needs no
     admin rights and turned off the unpacked test extension on YouTube in
     Edge. If force-installed extensions are affected too, the lock needs a
     fail-closed fallback, e.g. block youtube.com outright while the
     extension isn't reporting in from YouTube tabs.

## Environment note

This all writes to `HKLM`, i.e., machine-wide, real policy — not the
harmless per-user test key from `test-extension/`. It's fully reversible
(`unset-policies.ps1`), but while active it genuinely disables Incognito,
Guest mode, and DevTools on your real browsers, and Phase B force-installs
a real extension with no remove button until you delete the forcelist key.
Worth deciding up front whether you want to run this on your main PC
directly or in an isolated VM first.
