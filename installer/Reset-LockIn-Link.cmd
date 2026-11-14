@echo off
rem Double-click to forget the link to LockIn and update Smart Study Distraction Blocker.
rem For when LockIn has lost its secret and can't unlink itself. Windows asks for
rem administrator rights once. Afterwards, in LockIn, open Settings > Link Study
rem Blocker and click Link: the service leaves a fresh pairing offer.
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0install.ps1" -ResetLink
