# Removes Smart Study Distraction Blocker from this PC. Asks for
# administrator rights once. Refused while a lock is active: removing the
# blocker is the biggest loosening there is.
#
# Browser policy the service wrote (blocked sites, a lock's lockdown, and
# the forced install of the extension) is put back exactly as it was
# before the service took it over. Settings (the rules and channel lists)
# stay in C:\ProgramData for a later reinstall; add -RemoveSettings to delete
# them too.
param([switch]$RemoveSettings)

. (Join-Path $PSScriptRoot "common.ps1")
Assert-64Bit $PSCommandPath $(if ($RemoveSettings) { @("-RemoveSettings") } else { @() })

# Started from "Installed apps", this script lives inside the folder it is about
# to delete, so it runs from a temporary copy instead.
if ($PSScriptRoot.StartsWith($InstallDir, [StringComparison]::OrdinalIgnoreCase)) {
    $copy = Join-Path $env:TEMP "SmartStudyBlocker-uninstall"
    Remove-Item $copy -Recurse -Force -ErrorAction SilentlyContinue
    New-Item -ItemType Directory -Path $copy | Out-Null
    Copy-Item (Join-Path $PSScriptRoot "common.ps1"), $PSCommandPath $copy
    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $copy "uninstall.ps1") @(if ($RemoveSettings) { "-RemoveSettings" })
    exit $LASTEXITCODE
}
Assert-Elevated $PSCommandPath $(if ($RemoveSettings) { @("-RemoveSettings") } else { @() })

$ErrorActionPreference = "Stop"
try {
    Write-Host "Uninstalling $ProductName" -ForegroundColor Cyan

    $status = Invoke-Service "get-status"
    if ($null -ne $status -and $status.Ok -and $status.Payload.Lock.IsLocked) {
        throw "A lock is active, so uninstalling has to wait until it ends (or until a 'Break lock early' countdown finishes)."
    }
    if ($null -ne $status -and $status.Ok -and $status.Payload.Link.SessionActive) {
        throw "A LockIn session is running, so uninstalling has to wait until it ends."
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
    Remove-Item "HKLM:\$UninstallKey" -Force -ErrorAction SilentlyContinue
    Remove-Folder $InstallDir
    Remove-32BitCopy
    if ([System.Diagnostics.EventLog]::SourceExists($EventSource)) { Remove-EventLog -Source $EventSource }
    if ($RemoveSettings) {
        Remove-Folder $SettingsDir
        Write-Host "  settings deleted"
    } else {
        Write-Host "  settings kept in $SettingsDir"
    }

    Write-Host ""
    Write-Host "Done. $ProductName is uninstalled." -ForegroundColor Green
    Write-Host "  - Restart your browsers so they drop the blocks straight away. They remove the extension by themselves then."
    Write-Host "  - A copy you loaded unpacked yourself stays; remove it from the extensions page if you want it gone too."
} catch {
    Write-Host ""
    Write-Host "Uninstall stopped: $($_.Exception.Message)" -ForegroundColor Red
} finally {
    Read-Host "Press Enter to close"
}
