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
connects the browser extension, adds the shortcuts, starts the service and
opens the app. Running it again is how to update: same steps, and your
settings and any running lock carry on (they live in
`C:\ProgramData\SmartStudyBlocker\`, which the installer never touches).

A log of the last run is written to `last-install.log` here.

## Uninstall

Double-click `Uninstall.cmd`. It's refused while a lock is active.

It puts back every browser setting the service took over, exactly as it was,
then removes the service, the program folder, the shortcuts and the browser
extension's connection. Your settings stay in `C:\ProgramData\SmartStudyBlocker\`
for a later reinstall. To delete them too, run from PowerShell:
```powershell
powershell -ExecutionPolicy Bypass -File D:\Student-Study-Blocker\installer\uninstall.ps1 -RemoveSettings
```
The extension itself stays loaded in your browsers until you remove it from
their extensions page (it's loaded unpacked for now; see Milestone 0 Phase B).

## Files

- `Install.cmd` / `Uninstall.cmd`: double-click versions of the two scripts.
- `install.ps1`: install or update. Asks for administrator rights itself.
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
| native-messaging host, for the extension | `...\native-host\`, registered for Chrome, Edge and Brave under HKLM (every user) |
| settings, lock state, channel lists | `C:\ProgramData\SmartStudyBlocker\` (SYSTEM and administrators only) |
| log | Windows Event Viewer → Windows Logs → Application, source `SmartStudyBlockerService` |

- Everything under Program Files can only be changed by an administrator.
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
- Force-installing the extension (needs the store listings, Milestone 0
  Phase B).
