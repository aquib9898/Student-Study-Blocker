# Read-only, no admin needed: prints everything under each browser's policy
# key, plus any pre-test backup set-policies.ps1 has saved for it.

. "$PSScriptRoot\common.ps1"

function Show-Key($path, $indent) {
    $key = Get-Item -Path $path
    foreach ($name in $key.Property) {
        Write-Host ("{0}{1} = {2}" -f $indent, $name, $key.GetValue($name))
    }
    foreach ($child in Get-ChildItem -Path $path) {
        Write-Host ("{0}[{1}]" -f $indent, $child.PSChildName)
        Show-Key $child.PSPath "$indent  "
    }
}

foreach ($b in $Browsers) {
    Write-Host "== $($b.Name): $($b.Path) =="
    $psPath = "Registry::$($b.Path)"
    if (Test-Path $psPath) { Show-Key $psPath "  " } else { Write-Host "  (no policy key)" }
    if (Test-Path $BackupDir) {
        Get-ChildItem -Path $BackupDir -Filter "$($b.Name).*" | ForEach-Object {
            Write-Host "  saved pre-test state: backup\$($_.Name)"
        }
    }
    Write-Host ""
}
