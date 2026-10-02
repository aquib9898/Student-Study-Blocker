param(
    [Parameter(Mandatory = $true)]
    [string]$ExtensionId
)

$manifestPath = Join-Path $PSScriptRoot "com.smartstudyblocker.test.json"
$content = Get-Content $manifestPath -Raw
$content = $content -replace 'chrome-extension://[^"]*/', "chrome-extension://$ExtensionId/"
Set-Content -Path $manifestPath -Value $content -Encoding utf8 -NoNewline

Write-Host "allowed_origins now set to chrome-extension://$ExtensionId/"
Write-Host "Reload the extension (or the browser) and try the popup again."
