@echo off
rem Double-click to remove Smart Study Distraction Blocker (refused during a lock).
rem Windows asks for administrator rights once.
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0uninstall.ps1"
