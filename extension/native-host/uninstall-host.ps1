# Removes the native-messaging host registration for Chrome, Edge and Brave.
# Leaves the built exe and the manifest file in place.

$hostName = "com.smartstudyblocker.host"
$registryBases = @(
    "HKCU:\Software\Google\Chrome\NativeMessagingHosts",
    "HKCU:\Software\Microsoft\Edge\NativeMessagingHosts",
    "HKCU:\Software\BraveSoftware\Brave-Browser\NativeMessagingHosts"
)
foreach ($base in $registryBases) {
    $keyPath = Join-Path $base $hostName
    if (Test-Path $keyPath) {
        Remove-Item $keyPath -Force
        Write-Host "Removed: $keyPath"
    } else {
        Write-Host "Not registered: $keyPath"
    }
}
