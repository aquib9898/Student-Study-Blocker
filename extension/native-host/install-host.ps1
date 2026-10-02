# Builds the native-messaging host and registers it for Chrome, Edge and
# Brave. Per-user (HKCU): no admin rights, no browser policy touched. The
# host is only the bridge the extension uses to reach the service; the
# service itself must be installed and running separately.
#
# Run this once (a normal PowerShell window is fine), then load the unpacked
# extension from the folder above this one.

$ErrorActionPreference = "Stop"

$hostName = "com.smartstudyblocker.host"
$extensionId = "dkbfaenfbikncbcgecpekmpgcemijgob"  # fixed by the manifest "key"
$manifestPath = Join-Path $PSScriptRoot "$hostName.json"
$projectPath = Join-Path $PSScriptRoot "..\..\service\src\StudyBlocker.NativeHost\StudyBlocker.NativeHost.csproj"
$exePath = Join-Path $PSScriptRoot "..\..\service\src\StudyBlocker.NativeHost\bin\Release\net10.0\StudyBlocker.NativeHost.exe"

Write-Host "Building the native host..."
dotnet build $projectPath -c Release | Out-Null
if ($LASTEXITCODE -ne 0) { throw "Build failed." }
$exeFull = (Resolve-Path $exePath).Path
Write-Host "Host executable: $exeFull"

# Generate the manifest here so the path is always correct for wherever this
# repo actually sits. Written as UTF-8 with no BOM, which Chrome requires.
$manifest = [ordered]@{
    name            = $hostName
    description     = "Smart Study Distraction Blocker native-messaging bridge to the SYSTEM service"
    path            = $exeFull
    type            = "stdio"
    allowed_origins = @("chrome-extension://$extensionId/")
}
$json = $manifest | ConvertTo-Json -Depth 5
[System.IO.File]::WriteAllText($manifestPath, $json, (New-Object System.Text.UTF8Encoding($false)))
Write-Host "Wrote manifest: $manifestPath"

$registryBases = @(
    "HKCU:\Software\Google\Chrome\NativeMessagingHosts",
    "HKCU:\Software\Microsoft\Edge\NativeMessagingHosts",
    "HKCU:\Software\BraveSoftware\Brave-Browser\NativeMessagingHosts"
)
foreach ($base in $registryBases) {
    $keyPath = Join-Path $base $hostName
    New-Item -Path $keyPath -Force | Out-Null
    Set-Item -Path $keyPath -Value $manifestPath -Force
    Write-Host "Registered: $keyPath"
}

Write-Host ""
Write-Host "Done. Now load the unpacked extension from:"
Write-Host "  $((Resolve-Path (Join-Path $PSScriptRoot '..')).Path)"
Write-Host "Its ID is fixed at $extensionId by the manifest key, so there's no ID step."
