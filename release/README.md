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
