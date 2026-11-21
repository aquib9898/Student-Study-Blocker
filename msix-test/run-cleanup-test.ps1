# Test 3, run elevated (one UAC prompt): does a clean-up task, registered by the
# packaged service, survive the MSIX uninstall, remove the app's HKLM keys and
# then delete itself? Also asks Microsoft Defender whether it flagged anything.
$ErrorActionPreference = "Continue"
$out = "D:\Student-Study-Blocker\msix-test\out"
$taskName = "SSDB MSIX Probe Cleanup"
$started = Get-Date
Start-Transcript -Path "$out\cleanup-test-log.txt" -Force | Out-Null
Remove-Item "$out\service-log.txt", "$out\cleanup-log.txt" -ErrorAction SilentlyContinue

$probes = @(
    @{ What = "Chrome policy";         Key = "HKLM\SOFTWARE\Policies\Google\Chrome"; Value = "SSDBMsixProbe" },
    @{ What = "IFEO Debugger";         Key = "HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options\ssdb-msix-probe.exe"; Value = "Debugger" },
    @{ What = "Native-messaging host"; Key = "HKLM\SOFTWARE\Google\Chrome\NativeMessagingHosts\com.ssdb.msix_probe"; Value = $null }
)
function Get-Leftovers {
    $left = @()
    foreach ($p in $probes) {
        $regArgs = @("query", $p.Key, "/reg:64") + $(if ($p.Value) { @("/v", $p.Value) } else { @("/ve") })
        $r = & reg.exe @regArgs 2>&1 | Out-String
        if ($LASTEXITCODE -eq 0 -and $r -match "REG_SZ") { $left += $p.What }
    }
    & schtasks.exe /Query /TN $taskName 2>&1 | Out-Null
    if ($LASTEXITCODE -eq 0) { $left += "Scheduled task" }
    if (Test-Path "C:\ProgramData\SSDBMsixProbe") { $left += "ProgramData folder" }
    return $left
}

"=== 1. Install the MSIX; its service writes the keys and registers the clean-up task ==="
Import-Certificate -FilePath "$out\probe.cer" -CertStoreLocation Cert:\LocalMachine\TrustedPeople | Out-Null
try { Add-AppxPackage -Path "$out\probe.msix" -ErrorAction Stop; "Installed." } catch { "INSTALL FAILED: $($_.Exception.Message)" }
for ($i = 0; $i -lt 30 -and -not (Test-Path "$out\service-log.txt"); $i++) { Start-Sleep -Seconds 1 }
if (Test-Path "$out\service-log.txt") { Get-Content "$out\service-log.txt" } else { "(no service log)" }
"Present while installed: $((Get-Leftovers) -join ', ')"
"--- The registered task ---"
& schtasks.exe /Query /TN $taskName /V /FO LIST 2>&1 | Select-String "TaskName|Run As User|Task To Run|Repeat: Every|Status" | ForEach-Object { $_.Line.Trim() }

"=== 2. Let the task run once while the app is installed: it must change nothing ==="
& schtasks.exe /Run /TN $taskName | Out-Null
Start-Sleep -Seconds 8
"Present after a run with the app installed: $((Get-Leftovers) -join ', ')"
"Clean-up log so far: $(if (Test-Path "$out\cleanup-log.txt") { 'WROTE SOMETHING (bad)' } else { 'empty (good)' })"

"=== 3. Uninstall the MSIX and wait for the task (every minute) ==="
Get-AppxPackage -Name SSDB.MsixProbe | Remove-AppxPackage
$uninstalled = Get-Date
"Service still registered: $([bool](Get-Service SSDBMsixProbe -ErrorAction SilentlyContinue))"
"Left right after uninstall: $((Get-Leftovers) -join ', ')"
for ($i = 0; $i -lt 180 -and (Get-Leftovers).Count -gt 0; $i++) { Start-Sleep -Seconds 1 }
$left = Get-Leftovers
"Left after waiting $([int]((Get-Date) - $uninstalled).TotalSeconds) s: $(if ($left.Count) { $left -join ', ' } else { 'NOTHING' })"
"--- Clean-up log (written by the task as SYSTEM) ---"
if (Test-Path "$out\cleanup-log.txt") { Get-Content "$out\cleanup-log.txt" } else { "(the task never cleaned up)" }

"=== 4. Did Microsoft Defender flag anything during the test? ==="
$detections = @(Get-MpThreatDetection -ErrorAction SilentlyContinue | Where-Object { $_.InitialDetectionTime -ge $started })
if ($detections.Count) { $detections | Select-Object InitialDetectionTime, ThreatID, Resources | Format-List | Out-String } else { "No Defender detections since the test started." }

"=== 5. Safety net: remove anything the test left, untrust the certificate ==="
& reg.exe delete "HKLM\SOFTWARE\Policies\Google\Chrome" /v SSDBMsixProbe /f /reg:64 2>&1 | Out-Null
& reg.exe delete "HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options\ssdb-msix-probe.exe" /f /reg:64 2>&1 | Out-Null
& reg.exe delete "HKLM\SOFTWARE\Google\Chrome\NativeMessagingHosts\com.ssdb.msix_probe" /f /reg:64 2>&1 | Out-Null
& schtasks.exe /Delete /TN $taskName /F 2>&1 | Out-Null
Remove-Item "C:\ProgramData\SSDBMsixProbe" -Recurse -Force -ErrorAction SilentlyContinue
Get-AppxPackage -Name SSDB.MsixProbe | Remove-AppxPackage
Get-ChildItem Cert:\LocalMachine\TrustedPeople | Where-Object Subject -eq "CN=SSDB MSIX Probe" | Remove-Item
"Final leftovers: $(if ((Get-Leftovers).Count) { (Get-Leftovers) -join ', ' } else { 'none' }); package installed: $([bool](Get-AppxPackage -Name SSDB.MsixProbe)); certificate trusted: $([bool](Get-ChildItem Cert:\LocalMachine\TrustedPeople | Where-Object Subject -eq 'CN=SSDB MSIX Probe'))"
Stop-Transcript | Out-Null
