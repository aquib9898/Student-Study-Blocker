# Test 2, run elevated (one UAC prompt): can Chrome start a native-messaging
# host whose exe lives inside an MSIX? Installs the probe MSIX, registers three
# test hosts (inside the package by path, inside it by app execution alias, and
# a control copy outside any package), opens a throwaway Chrome for Testing
# window at normal (non-admin) level with a test extension that calls all
# three, collects the answers, then removes every trace.
$ErrorActionPreference = "Continue"
$root = "D:\Student-Study-Blocker\msix-test"
$out = "$root\out"
Start-Transcript -Path "$out\host-test-log.txt" -Force | Out-Null
Remove-Item "$out\host-log.txt", "$out\results.json" -ErrorAction SilentlyContinue
$extId = (Get-Content "$out\ext-id.txt" -Raw).Trim()
$chrome = "$env:USERPROFILE\.cache\puppeteer\chrome\win64-148.0.7778.97\chrome-win64\chrome.exe"
$profileDir = "$out\chrome-profile"
$regRoots = @("HKCU:\Software\Chromium\NativeMessagingHosts", "HKCU:\Software\Google\Chrome\NativeMessagingHosts")

"=== 1. Install the MSIX ==="
Import-Certificate -FilePath "$out\probe.cer" -CertStoreLocation Cert:\LocalMachine\TrustedPeople | Out-Null
try { Add-AppxPackage -Path "$out\probe.msix" -ErrorAction Stop; "Installed." } catch { "INSTALL FAILED: $($_.Exception.Message)" }
$pkg = Get-AppxPackage -Name SSDB.MsixProbe
"Install location: $($pkg.InstallLocation)"
$alias = "$env:LOCALAPPDATA\Microsoft\WindowsApps\ssdb-msix-host.exe"
"Alias exists: $(Test-Path $alias)  ($alias)"

"=== 2. Register three test hosts (only the test extension may call them) ==="
$hosts = [ordered]@{
    "com.ssdb.msix_control" = "$root\layout\Probe.exe"
    "com.ssdb.msix_direct"  = "$($pkg.InstallLocation)\Probe.exe"
    "com.ssdb.msix_alias"   = $alias
}
New-Item -ItemType Directory -Force "$out\hosts" | Out-Null
foreach ($name in $hosts.Keys) {
    $manifest = [ordered]@{ name = $name; description = "SSDB MSIX test"; path = $hosts[$name]; type = "stdio"; allowed_origins = @("chrome-extension://$extId/") }
    $file = "$out\hosts\$name.json"
    [IO.File]::WriteAllText($file, ($manifest | ConvertTo-Json))   # no BOM: Chrome's parser rejects one
    foreach ($r in $regRoots) { New-Item -Path "$r\$name" -Force | Out-Null; Set-Item -Path "$r\$name" -Value $file }
    "{0,-24} -> {1}" -f $name, $hosts[$name]
}

"=== 3. Open Chrome for Testing at normal level (through Explorer) and wait for the answers ==="
$launch = "$out\launch.cmd"
[IO.File]::WriteAllText($launch, "@start `"`" `"$chrome`" --user-data-dir=`"$profileDir`" --load-extension=`"$root\ext`" --disable-extensions-except=`"$root\ext`" --no-first-run --no-default-browser-check about:blank`r`n")
Start-Process explorer.exe -ArgumentList "`"$launch`""
for ($i = 0; $i -lt 90 -and -not (Test-Path "$out\results.json"); $i++) { Start-Sleep -Seconds 1 }
Start-Sleep -Seconds 2
"--- Every host launch Chrome made (host-log.txt) ---"
if (Test-Path "$out\host-log.txt") { Get-Content "$out\host-log.txt" } else { "(no host was ever launched)" }
"--- What the extension got back (results.json) ---"
if (Test-Path "$out\results.json") { (Get-Content "$out\results.json" -Raw | ConvertFrom-Json).results | ConvertTo-Json -Depth 5 } else { "(no results after 90 s)" }

"=== 4. Clean up ==="
Get-CimInstance Win32_Process -Filter "Name='chrome.exe'" | Where-Object { $_.CommandLine -like "*msix-test\out\chrome-profile*" } |
    ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
Get-Process Probe -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
Start-Sleep -Seconds 2
Get-AppxPackage -Name SSDB.MsixProbe | Remove-AppxPackage
foreach ($name in $hosts.Keys) { foreach ($r in $regRoots) { Remove-Item -Path "$r\$name" -Force -ErrorAction SilentlyContinue } }
Get-ChildItem Cert:\LocalMachine\TrustedPeople | Where-Object Subject -eq "CN=SSDB MSIX Probe" | Remove-Item
Remove-Item $profileDir -Recurse -Force -ErrorAction SilentlyContinue
"Package still installed: $([bool](Get-AppxPackage -Name SSDB.MsixProbe))"
"Alias still there: $(Test-Path $alias)"
"Test host keys left: $(@(foreach ($name in $hosts.Keys) { foreach ($r in $regRoots) { if (Test-Path "$r\$name") { "$r\$name" } } }).Count)"
"Test certificate still trusted: $([bool](Get-ChildItem Cert:\LocalMachine\TrustedPeople | Where-Object Subject -eq 'CN=SSDB MSIX Probe'))"
Stop-Transcript | Out-Null
