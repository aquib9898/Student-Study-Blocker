#Requires -RunAsAdministrator
# Applies real Chrome/Edge/Brave policies to this machine (see common.ps1).
# Does not attempt ExtensionInstallForcelist: on a non-domain PC that needs
# a store-hosted extension (see README).
#
# The first run saves each browser's policy key to .\backup\ before touching
# it. Every run rebuilds each key from that saved state plus the test
# policies, so re-running after editing common.ps1 leaves nothing stale.

. "$PSScriptRoot\common.ps1"
New-Item -ItemType Directory -Path $BackupDir -Force | Out-Null

foreach ($b in $Browsers) { Save-PolicyBackup $b $BackupDir }

foreach ($b in $Browsers) {
    $null = Restore-PolicyKey $b $BackupDir
    Add-TestPolicies $b
    Write-Host "Applied test policies to $($b.Name) at $($b.Path)"
}

Write-Host ""
Write-Host "Now open each browser and check chrome://policy, edge://policy and"
Write-Host "brave://policy - every policy should show status OK."
