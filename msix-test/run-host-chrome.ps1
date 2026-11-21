# Test 2, the non-admin half: once the MSIX is installed, register the three
# test hosts for this user, open a throwaway Chrome for Testing with the test
# extension, collect the answers, then remove the hosts and signal "done".
$root = "D:\Student-Study-Blocker\msix-test"
$out = "$root\out"
Remove-Item "$out\host-log.txt", "$out\results.json" -ErrorAction SilentlyContinue
$extId = (Get-Content "$out\ext-id.txt" -Raw).Trim()
$installLocation = (Get-Content "$out\installed.flag" -Raw).Trim()
$alias = "$env:LOCALAPPDATA\Microsoft\WindowsApps\ssdb-msix-host.exe"
$regRoots = @("HKCU:\Software\Chromium\NativeMessagingHosts", "HKCU:\Software\Google\Chrome\NativeMessagingHosts")
$hosts = [ordered]@{
    "com.ssdb.msix_control" = "$root\layout\Probe.exe"
    "com.ssdb.msix_direct"  = "$installLocation\Probe.exe"
    "com.ssdb.msix_alias"   = $alias
}
"Alias exists: $(Test-Path $alias)"
foreach ($name in $hosts.Keys) {
    $manifest = [ordered]@{ name = $name; description = "SSDB MSIX test"; path = $hosts[$name]; type = "stdio"; allowed_origins = @("chrome-extension://$extId/") }
    $file = "$out\hosts\$name.json"
    [IO.File]::WriteAllText($file, ($manifest | ConvertTo-Json))
    foreach ($r in $regRoots) { New-Item -Path "$r\$name" -Force | Out-Null; Set-Item -Path "$r\$name" -Value $file }
    "{0,-24} -> {1}" -f $name, $hosts[$name]
}

$chrome = "$env:USERPROFILE\.cache\puppeteer\chrome\win64-148.0.7778.97\chrome-win64\chrome.exe"
$profileDir = "$out\chrome-profile"
Start-Process $chrome -ArgumentList "--user-data-dir=`"$profileDir`"", "--load-extension=`"$root\ext`"", "--disable-extensions-except=`"$root\ext`"", "--no-first-run", "--no-default-browser-check", "about:blank" | Out-Null
for ($i = 0; $i -lt 60 -and -not (Test-Path "$out\results.json"); $i++) { Start-Sleep -Seconds 1 }
Start-Sleep -Seconds 2
"--- Every host launch Chrome made ---"
if (Test-Path "$out\host-log.txt") { Get-Content "$out\host-log.txt" } else { "(none)" }
"--- What the extension got back ---"
if (Test-Path "$out\results.json") { (Get-Content "$out\results.json" -Raw | ConvertFrom-Json).results | ConvertTo-Json -Depth 5 } else { "(no results after 60 s)" }

Get-CimInstance Win32_Process -Filter "Name='chrome.exe'" | Where-Object { $_.CommandLine -like "*msix-test\out\chrome-profile*" } |
    ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
Start-Sleep -Seconds 2
foreach ($name in $hosts.Keys) { foreach ($r in $regRoots) { Remove-Item -Path "$r\$name" -Force -ErrorAction SilentlyContinue } }
Remove-Item $profileDir -Recurse -Force -ErrorAction SilentlyContinue
"Test host keys left: $(@(foreach ($name in $hosts.Keys) { foreach ($r in $regRoots) { if (Test-Path "$r\$name") { 1 } } }).Count)"
[IO.File]::WriteAllText("$out\done.flag", "ok")
