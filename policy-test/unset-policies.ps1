#Requires -RunAsAdministrator
# Puts each browser's policy key back exactly as set-policies.ps1 first found
# it, then deletes the backups so the next set run takes a fresh one.

. "$PSScriptRoot\common.ps1"

foreach ($b in $Browsers) {
    if (Restore-PolicyKey $b $BackupDir) {
        Remove-Item -Path (Join-Path $BackupDir "$($b.Name).*") -Force
        Write-Host "Restored $($b.Name) ($($b.Path)) to its pre-test state"
    } else {
        Write-Host "No saved state for $($b.Name); left $($b.Path) untouched"
    }
}

Write-Host ""
Write-Host "Fully quit and reopen each browser for this to take effect."
