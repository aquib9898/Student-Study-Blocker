# Removes Smart Study Distraction Blocker from this PC. Asks for
# administrator rights once. Refused while a lock is active: removing the
# blocker is the biggest loosening there is.
#
# Browser policy the service wrote (blocked sites, and a lock's lockdown) is
# put back exactly as it was before the service took it over. Settings (the
# rules and channel lists) stay in C:\ProgramData for a later reinstall;
# add -RemoveSettings to delete them too.
param([switch]$RemoveSettings)

. (Join-Path $PSScriptRoot "common.ps1")
Assert-Elevated $PSCommandPath $(if ($RemoveSettings) { @("-RemoveSettings") } else { @() })

$ErrorActionPreference = "Stop"
try {
    Write-Host "Uninstalling $ProductName" -ForegroundColor Cyan

    $status = Invoke-Service "get-status"
    if ($null -ne $status -and $status.Ok -and $status.Payload.Lock.IsLocked) {
        throw "A lock is active, so uninstalling has to wait until it ends (or until a 'Break lock early' countdown finishes)."
    }

    Write-Step "Stopping what's running..."
    Stop-Everything

    if (Test-Path $ServiceExe) {
        Write-Step "Putting browser policy back as it was..."
        # The service checks for a lock itself, in case it wasn't running
        # to ask above.
        & $ServiceExe --restore-browser-policies
        if ($LASTEXITCODE -ne 0) {
            Start-Service -Name $ServiceName -ErrorAction SilentlyContinue
            throw "The service didn't put browser policy back (exit code $LASTEXITCODE), so nothing was removed."
        }
    }

    Write-Step "Removing..."
    if (Get-Service -Name $ServiceName -ErrorAction SilentlyContinue) {
        & sc.exe delete $ServiceName | Out-Null
    }
    foreach ($key in $NativeHostKeys) {
        if (Test-Path "HKLM:\$key") { Remove-Item "HKLM:\$key" -Force }
    }
    foreach ($shortcutPath in $StartMenuShortcut, $DesktopShortcut) {
        if (Test-Path $shortcutPath) { Remove-Item $shortcutPath -Force }
    }
    Remove-Folder $InstallDir
    if ([System.Diagnostics.EventLog]::SourceExists($EventSource)) { Remove-EventLog -Source $EventSource }
    if ($RemoveSettings) {
        Remove-Folder $SettingsDir
        Write-Host "  settings deleted"
    } else {
        Write-Host "  settings kept in $SettingsDir"
    }

    Write-Host ""
    Write-Host "Done. $ProductName is uninstalled." -ForegroundColor Green
    Write-Host "  - Restart your browsers so they drop the blocks straight away."
    Write-Host "  - The extension is still loaded in your browsers; remove it from their extensions page if you want it gone too."
} catch {
    Write-Host ""
    Write-Host "Uninstall stopped: $($_.Exception.Message)" -ForegroundColor Red
} finally {
    Read-Host "Press Enter to close"
}
