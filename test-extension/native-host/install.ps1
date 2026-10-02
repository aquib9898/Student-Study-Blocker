# Registers the native messaging host manifest for Chrome, Edge, and Brave.
# Per-user (HKCU) on purpose: this only proves the extension<->host<->registry
# mechanism works. It does NOT need admin rights and does NOT touch any real
# browser policy. Run this once, then load the unpacked extension and run
# set-extension-id.ps1.

$hostName = "com.smartstudyblocker.test"
$manifestPath = Join-Path $PSScriptRoot "$hostName.json"

if (-not (Test-Path $manifestPath)) {
    Write-Error "Host manifest not found at $manifestPath"
    exit 1
}

$registryBases = @(
    "HKCU:\Software\Google\Chrome\NativeMessagingHosts",
    "HKCU:\Software\Microsoft\Edge\NativeMessagingHosts",
    "HKCU:\Software\BraveSoftware\Brave-Browser\NativeMessagingHosts"
)

foreach ($base in $registryBases) {
    $keyPath = Join-Path $base $hostName
    New-Item -Path $keyPath -Force | Out-Null
    Set-Item -Path $keyPath -Value $manifestPath -Force
    Write-Host "Registered: $keyPath -> $manifestPath"
}

Write-Host ""
Write-Host "Next steps:"
Write-Host "1. Load the extension folder as an unpacked extension in your browser."
Write-Host "2. Copy its extension ID from the extensions page."
Write-Host "3. Run: .\set-extension-id.ps1 -ExtensionId <the-id>"
