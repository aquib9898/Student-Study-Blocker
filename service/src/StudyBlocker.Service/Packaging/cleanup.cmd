@echo off
rem Smart Study Distraction Blocker: uninstall clean-up, run as SYSTEM by the
rem scheduled task "Smart Study Distraction Blocker uninstall clean-up".
rem
rem The Microsoft Store (MSIX) version runs no code of its own when it is
rem uninstalled, so this does that part. While the app's service is installed
rem it does nothing. Once the service has been gone for two minutes (an update
rem only removes it for a moment), it:
rem   1. puts back every browser setting the app had changed (restore.reg,
rem      which the service keeps up to date), and removes its native-host and
rem      event-log registrations;
rem   2. removes the app's folder in Program Files;
rem   3. deletes this task, then this folder.
rem The app's own settings in C:\ProgramData\SmartStudyBlocker stay, for a
rem reinstall, just as with the EXE setup's uninstaller.
sc.exe query SmartStudyBlockerService >nul 2>&1 && exit /b 0
ping -n 121 127.0.0.1 >nul
sc.exe query SmartStudyBlockerService >nul 2>&1 && exit /b 0
reg.exe import "%~dp0restore.reg" /reg:64
rd /s /q "%ProgramFiles%\Smart Study Distraction Blocker"
schtasks.exe /Delete /TN "Smart Study Distraction Blocker uninstall clean-up" /F
(goto) 2>nul & rd /s /q "%~dp0"
