# Test 2, the admin half (one UAC prompt): trust the test certificate and
# install the probe MSIX, signal "installed", wait until the non-admin half
# (Chrome test) signals "done", then uninstall and untrust everything.
$ErrorActionPreference = "Continue"
$out = "D:\Student-Study-Blocker\msix-test\out"
Start-Transcript -Path "$out\host-admin-log.txt" -Force | Out-Null
Remove-Item "$out\installed.flag", "$out\done.flag", "$out\cleaned.flag" -ErrorAction SilentlyContinue

Import-Certificate -FilePath "$out\probe.cer" -CertStoreLocation Cert:\LocalMachine\TrustedPeople | Out-Null
try { Add-AppxPackage -Path "$out\probe.msix" -ErrorAction Stop; "Installed." } catch { "INSTALL FAILED: $($_.Exception.Message)" }
$pkg = Get-AppxPackage -Name SSDB.MsixProbe
"Install location: $($pkg.InstallLocation)"
[IO.File]::WriteAllText("$out\installed.flag", "$($pkg.InstallLocation)")

for ($i = 0; $i -lt 600 -and -not (Test-Path "$out\done.flag"); $i++) { Start-Sleep -Seconds 1 }
"Chrome test finished: $(Test-Path "$out\done.flag")"

Get-Process Probe -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
Get-AppxPackage -Name SSDB.MsixProbe | Remove-AppxPackage
Get-ChildItem Cert:\LocalMachine\TrustedPeople | Where-Object Subject -eq "CN=SSDB MSIX Probe" | Remove-Item
"Package still installed: $([bool](Get-AppxPackage -Name SSDB.MsixProbe))"
"Alias still there: $(Test-Path "$env:LOCALAPPDATA\Microsoft\WindowsApps\ssdb-msix-host.exe")"
"Test certificate still trusted: $([bool](Get-ChildItem Cert:\LocalMachine\TrustedPeople | Where-Object Subject -eq 'CN=SSDB MSIX Probe'))"
Stop-Transcript | Out-Null
[IO.File]::WriteAllText("$out\cleaned.flag", "ok")
