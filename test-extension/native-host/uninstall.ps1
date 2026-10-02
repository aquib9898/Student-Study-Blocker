# Removes the native messaging host registration and the harmless test
# registry key created by host.js. Safe to run any time.

$hostName = "com.smartstudyblocker.test"
$registryBases = @(
    "HKCU:\Software\Google\Chrome\NativeMessagingHosts",
    "HKCU:\Software\Microsoft\Edge\NativeMessagingHosts",
    "HKCU:\Software\BraveSoftware\Brave-Browser\NativeMessagingHosts"
)

foreach ($base in $registryBases) {
    $keyPath = Join-Path $base $hostName
    if (Test-Path $keyPath) {
        Remove-Item -Path $keyPath -Force
        Write-Host "Removed: $keyPath"
    }
}

$testKey = "HKCU:\Software\SmartStudyBlockerTest"
if (Test-Path $testKey) {
    Remove-Item -Path $testKey -Force -Recurse
    Write-Host "Removed test value key: $testKey"
}
