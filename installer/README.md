# installer — install, update, uninstall (Milestone 9, part 1)

Puts Smart Study Distraction Blocker on this PC properly, so nothing needs
starting from PowerShell any more. You give administrator rights once, when
installing; after that:
- **The service** starts by itself with Windows, before anyone signs in, as
  SYSTEM. It has no window. If it stops unexpectedly (a crash, or its process
  being ended), Windows starts it again after 5 seconds. Only an administrator
  can stop it on purpose.
- **The app** opens from the Start menu or the desktop like any other program,
  never asking for rights. It only asks the service for changes (plan §6), so
  closing it changes nothing.

## Install, or update after a change

Double-click `Install.cmd` in this folder and click **Yes** on the Windows
prompt. The window that opens shows each step and waits for Enter at the end.

It builds everything from this repo, stops what's running, copies it to
`C:\Program Files\Smart Study Distraction Blocker\`, registers the service,
connects the browser extension (the service force-installs it in Chrome, Edge
and Brave, for every profile), adds the shortcuts, starts the service and
opens the app. Running it again is how to update: same steps, and your
settings and any running lock carry on (they live in
`C:\ProgramData\SmartStudyBlocker\`, which the installer never touches).

A log of the last run is written to `last-install.log` here.

## Uninstall

Double-click `Uninstall.cmd`. It's refused while a lock is active, or while
a LockIn session is running.

It puts back every browser setting the service took over, exactly as it was,
then removes the service, the program folder, the shortcuts and the browser
extension's connection. Your settings stay in `C:\ProgramData\SmartStudyBlocker\`
for a later reinstall. To delete them too, run from PowerShell:
```powershell
powershell -ExecutionPolicy Bypass -File D:\Student-Study-Blocker\installer\uninstall.ps1 -RemoveSettings
```
Chrome, Edge and Brave remove the extension by themselves once restarted
(the service was force-installing it). An unpacked copy you loaded yourself
stays until you remove it from the extensions page.

## Files

- `Install.cmd` / `Uninstall.cmd`: double-click versions of the two scripts.
- `Reset-LockIn-Link.cmd`: double-click version of `install.ps1 -ResetLink`
  (below). The app's reload arrow (top right) does the same without
  administrator rights, except during a LockIn session, and LockIn's messages
  point to that.
- `install.ps1`: install or update. Asks for administrator rights itself, and
  starts itself again in the 64-bit PowerShell when a 32-bit one ran it (that
  one installs into Program Files (x86), where nothing looks). It also clears
  away what the first 0.3.2 setup left there.
  `-ResetLink` also forgets the link to LockIn (the service then leaves a
  fresh pairing offer): for when LockIn has lost its secret and can't
  unlink itself. Only an administrator can do that.
- `uninstall.ps1`: remove (`-RemoveSettings` also deletes the settings).
- `build.ps1`: builds the three programs into a folder, no rights needed.
  `install.ps1` runs it first, into `%TEMP%`, never touching the repo's own
  `bin`/`obj` folders.
- `common.ps1`: the names and places both scripts use, and their helpers.

## What gets installed where

| What | Where |
|---|---|
| service (`StudyBlocker.Service.exe`) | `C:\Program Files\Smart Study Distraction Blocker\service\`, Windows service `SmartStudyBlockerService`, shown as "Smart Study Distraction Blocker" |
| app (`SmartStudyDistractionBlocker.exe`) | `...\app\`, Start menu and public desktop shortcuts |
| native-messaging host, for the extension | `...\native-host\`, registered for Chrome, Edge and Brave under HKLM (every user). It answers the unpacked copy and both store copies (`$ExtensionIds` in `common.ps1`) |
| pairing offer for LockIn | `...\link\pairing-offer.json`, written by the service while nothing is linked. Every user can read the folder; only administrators and the service can write it |
| settings, lock state, channel lists, the LockIn link | `C:\ProgramData\SmartStudyBlocker\` (SYSTEM and administrators only) |
| log | Windows Event Viewer → Windows Logs → Application, source `SmartStudyBlockerService` |

- Everything under Program Files can only be changed by an administrator.
- Any signed-in user may **start** the service (so LockIn can bring it back
  if it was stopped), and nothing more: `Grant-ServiceStart` in `common.ps1`
  adds just the start right for Authenticated Users to the service's
  security descriptor. Stopping it, pausing it and changing it stay with
  administrators.
- The `link\` folder is made once and never replaced by an update, so a
  pairing offer waiting for LockIn survives it.
- The installer removes the per-user host registration from development
  (`extension\native-host\install-host.ps1`), because a per-user one takes
  priority over the HKLM one.
- It stops a copy of the service started with `dotnet run` from an earlier
  milestone. The service now also refuses to run a second copy next to the
  installed one, so that old command just says so and exits.
- The programs use the .NET 10 runtime already on this PC
  (framework-dependent). An installer for other PCs would bundle it or check
  for it; that's for later in Milestone 9.

## Reading the service's log

With no console window, the service writes to the Windows Application event
log (information and up). Either open Event Viewer (Win+R, `eventvwr`) →
Windows Logs → Application and look for source `SmartStudyBlockerService`,
or from PowerShell:
```powershell
Get-WinEvent -FilterHashtable @{ LogName = 'Application'; ProviderName = 'SmartStudyBlockerService' } -MaxEvents 20 | Format-List TimeCreated, Message
```

## Not done yet (rest of Milestone 9)

- The account-setup guide: a separate Admin account, and the daily account
  made Standard (plan §3). Until then the daily account is an administrator,
  and an administrator can always stop the service or uninstall.
- The Local AI tier's hardware test and opt-in.
