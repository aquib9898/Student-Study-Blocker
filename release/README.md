# release: the one-file installer

`build-setup.ps1` builds `out/Study-Blocker-Setup-<version>.exe`, the file to hand
to someone who isn't technical (the user's request, 2026-10-24).

- Double-click it, click Next, say yes to the one Windows prompt, wait a minute.
  It installs the service, the app, the extension's connection to the browsers,
  the Start menu and desktop shortcuts, and an entry in Windows' "Installed apps"
  (that's how it's removed again).
- Everything is inside the one file, including the .NET runtime: the programs are
  published self-contained, so nothing else needs installing.
- Updating is the same file run again: settings, a running lock and the LockIn
  link are kept.
- It wraps `../installer/install.ps1` (`-PrebuiltDir ... -Quiet`), so the
  double-click installers beside it and this one install exactly the same way.
- It runs that script in the 64-bit PowerShell, through `Sysnative`. The setup
  program itself is 32-bit, and the PowerShell Windows hands a 32-bit program
  installs into Program Files (x86) and the 32-bit half of the registry. The
  first 0.3.2 setup did exactly that, so the service never found the folder for
  LockIn's pairing offer and LockIn could never link. Running a fixed setup over
  it moves everything back and clears the leftovers.
- The version comes from `<Version>` in the service's project file.
- Needs NSIS to build: it uses the copy LockIn's `npm run dist` downloads, or
  NSIS on the PATH. `out/` is not committed.
- The setup file isn't code-signed, so Windows SmartScreen may say "unknown
  publisher" the first time (More info > Run anyway) until it is.

# The Microsoft Store package (MSIX)

`build-msix.ps1` builds `out/SmartStudyDistractionBlocker-<version>.msix` (the
user chose the MSIX route for the Microsoft Store, 2026-11-21, accepting a
somewhat weaker lock). No administrator rights or Windows SDK needed: makeappx
and signtool come from NuGet (`msix/tools`).

- Same three self-contained programs as the setup, in the same folders
  (`service`, `native-host`, `app`), plus `msix/AppxManifest.template.xml` and
  the tile images in `msix/Assets`. About 140 MB: each program carries its own
  .NET runtime.
- For the Store: `.\build-msix.ps1 -IdentityName <Package/Identity/Name>
  -Publisher "<Package/Identity/Publisher>" -PublisherDisplayName "<name>"`,
  all three from Partner Center > Product management > Product identity, then
  upload the unsigned .msix (the Store signs it). Partner Center will ask why
  the package needs the restricted capabilities `packagedServices` and
  `localSystemServices`: a SYSTEM service is what enforces blocking and locks
  for every account.
- To try it on a PC: add `-CertificatePfx <file> -CertificatePassword <pw>`
  with a code-signing certificate whose subject is exactly `-Publisher`, trust
  it (Local Machine > Trusted People), and **uninstall the EXE setup first**:
  both register the service `SmartStudyBlockerService`, so the package won't
  install next to it.

How it differs from the EXE setup (all in
`service/src/StudyBlocker.Service/Packaging`, tested with throwaway packages
2026-11-21, see `../msix-test` if it's still there):

- MSIX runs no code at install or uninstall. The service, when it runs from
  the package, does install.ps1's part itself at every start: registers the
  native host for Chrome, Edge and Brave (manifest in `C:\Program Files\Smart
  Study Distraction Blocker\native-host`, pointing into the package, whose
  folder name changes with every version), writes `app-path.txt` there for
  LockIn, sets the service's restart-on-failure and lets signed-in users start
  it. LockIn's pairing offer goes in the same Program Files folder as before.
- Uninstalling leaves registry changes behind, so the service also keeps an
  uninstall clean-up ready in `C:\ProgramData\SmartStudyBlocker\uninstall`
  (locked to SYSTEM and administrators): `restore.reg`, rewritten whenever the
  policy ledger changes, `cleanup.cmd`, and the SYSTEM scheduled task
  "Smart Study Distraction Blocker uninstall clean-up" (start-up, sign-in,
  every 10 minutes). It does nothing while the service exists; once the
  service has been gone for two minutes it imports `restore.reg` (browser
  settings back as they were, native-host and event-log keys gone), deletes
  the Program Files folder, the task and itself. Settings in ProgramData stay,
  as with the EXE uninstaller.
- No "Image File Execution Options" keys: a leftover one would stop a blocked
  program from ever opening. Blocked programs are closed within a second by
  the service instead (with the app's notice), so they may flash up briefly.
- An uninstall isn't refused during a lock, as `uninstall.ps1` refuses it:
  Windows removes the package regardless, and the clean-up then ends the lock.
