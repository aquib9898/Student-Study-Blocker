# Runs elevated (one UAC prompt). Installs the probe MSIX, checks from outside
# the package whether its packaged service reached the REAL registry, then
# uninstalls it, checks what was left behind, and removes every trace.
$ErrorActionPreference = "Continue"
$dir = "D:\Student-Study-Blocker\msix-test\out"
Start-Transcript -Path "$dir\elevated-log.txt" -Force | Out-Null

Add-Type -Namespace Probe -Name Native -MemberDefinition @'
[DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
public static extern int GetCurrentPackageFullName(ref int length, System.Text.StringBuilder name);
'@
$len = 0
"This checking shell has package identity: $([Probe.Native]::GetCurrentPackageFullName([ref]$len, $null) -eq 122)"

$probes = @(
    @{ What = "Chrome policy";          Key = "HKLM\SOFTWARE\Policies\Google\Chrome"; Value = "SSDBMsixProbe" },
    @{ What = "IFEO Debugger";          Key = "HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options\ssdb-msix-probe.exe"; Value = "Debugger" },
    @{ What = "Native-messaging host";  Key = "HKLM\SOFTWARE\Google\Chrome\NativeMessagingHosts\com.ssdb.msix_probe"; Value = $null }
)
function Show-Registry($label) {
    "--- Real registry ($label) ---"
    foreach ($p in $probes) {
        $regArgs = @("query", $p.Key, "/reg:64")
        if ($p.Value) { $regArgs += @("/v", $p.Value) } else { $regArgs += "/ve" }
        $result = & reg.exe @regArgs 2>&1 | Out-String
        $found = $LASTEXITCODE -eq 0 -and $result -match "REG_SZ"
        "{0,-24} {1}" -f $p.What, $(if ($found) { "PRESENT: " + (($result -split "`n" | Where-Object { $_ -match "REG_SZ" }) -join "").Trim() } else { "absent" })
    }
    "ProgramData file         " + $(if (Test-Path "C:\ProgramData\SSDBMsixProbe\probe.txt") { "PRESENT" } else { "absent" })
}

"=== 1. Trust the test certificate and install the MSIX ==="
Import-Certificate -FilePath "$dir\probe.cer" -CertStoreLocation Cert:\LocalMachine\TrustedPeople | Out-Null
Remove-Item "$dir\service-log.txt" -ErrorAction SilentlyContinue
try { Add-AppxPackage -Path "$dir\probe.msix" -ErrorAction Stop; "Installed." } catch { "INSTALL FAILED: $($_.Exception.Message)" }
Get-AppxPackage -Name SSDB.MsixProbe | Select-Object PackageFullName, InstallLocation | Format-List | Out-String

"=== 2. The packaged service ==="
for ($i = 0; $i -lt 30 -and -not (Test-Path "$dir\service-log.txt"); $i++) { Start-Sleep -Seconds 1 }
Get-Service SSDBMsixProbe -ErrorAction SilentlyContinue | Select-Object Name, Status, StartType | Format-List | Out-String
& sc.exe qc SSDBMsixProbe | Out-String
"--- What the service itself reported ---"
if (Test-Path "$dir\service-log.txt") { Get-Content "$dir\service-log.txt" } else { "(no service log after 30 s)" }
Show-Registry "while installed"

"=== 3. Uninstall the MSIX ==="
Get-AppxPackage -Name SSDB.MsixProbe | Remove-AppxPackage
Start-Sleep -Seconds 5
"Package still installed: $([bool](Get-AppxPackage -Name SSDB.MsixProbe))"
"Service still registered: $([bool](Get-Service SSDBMsixProbe -ErrorAction SilentlyContinue))"
Show-Registry "after uninstall: what MSIX left behind"

"=== 4. Clean up every trace ==="
& reg.exe delete "HKLM\SOFTWARE\Policies\Google\Chrome" /v SSDBMsixProbe /f /reg:64 2>&1 | Out-Null
& reg.exe delete "HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options\ssdb-msix-probe.exe" /f /reg:64 2>&1 | Out-Null
& reg.exe delete "HKLM\SOFTWARE\Google\Chrome\NativeMessagingHosts\com.ssdb.msix_probe" /f /reg:64 2>&1 | Out-Null
Remove-Item "C:\ProgramData\SSDBMsixProbe" -Recurse -Force -ErrorAction SilentlyContinue
Get-ChildItem Cert:\LocalMachine\TrustedPeople | Where-Object Subject -eq "CN=SSDB MSIX Probe" | Remove-Item
"Test certificate still trusted: $([bool](Get-ChildItem Cert:\LocalMachine\TrustedPeople | Where-Object Subject -eq 'CN=SSDB MSIX Probe'))"
Show-Registry "after clean-up"
Stop-Transcript | Out-Null
