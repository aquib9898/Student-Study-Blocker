# SSDB MSIX Probe clean-up (runs as SYSTEM from Task Scheduler).
# While the app's service is registered it does nothing. Once the app has been
# uninstalled, it removes the settings the app wrote, then deletes itself.
$log = 'D:\Student-Study-Blocker\msix-test\out\cleanup-log.txt'
if (Get-Service SSDBMsixProbe -ErrorAction SilentlyContinue) { exit 0 }
Add-Content $log "$(Get-Date -Format o) service gone: removing the app's settings"
reg.exe delete "HKLM\SOFTWARE\Policies\Google\Chrome" /v SSDBMsixProbe /f /reg:64 | Out-Null
reg.exe delete "HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options\ssdb-msix-probe.exe" /f /reg:64 | Out-Null
reg.exe delete "HKLM\SOFTWARE\Google\Chrome\NativeMessagingHosts\com.ssdb.msix_probe" /f /reg:64 | Out-Null
schtasks.exe /Delete /TN "SSDB MSIX Probe Cleanup" /F | Out-Null
Add-Content $log "$(Get-Date -Format o) task deleted; removing its own folder"
Remove-Item 'C:\ProgramData\SSDBMsixProbe' -Recurse -Force
