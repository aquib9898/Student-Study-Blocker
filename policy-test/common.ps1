# Paths are in reg.exe form so the same string works with reg.exe and with
# PowerShell's Registry:: provider.

$BackupDir = Join-Path $PSScriptRoot "backup"

$CommonPolicies = [ordered]@{
    BrowserGuestModeEnabled    = 0      # guest mode off
    DeveloperToolsAvailability = 2      # DevTools blocked entirely
    DnsOverHttpsMode           = "off"  # secure DNS locked off
    # DevTools=2 also locks extension Developer mode unless this is set.
    # 0 lets us load the unpacked test extension; a real lock would use 1.
    ExtensionDeveloperModeSettings = 0
}

$TestBlocklist = @("example.com", "youtube.com/shorts")

# Private-window policies differ: Edge renamed Chrome's
# IncognitoModeAvailability, and Brave's Tor windows need their own switch.
# Edge ignores ExtensionDeveloperModeSettings on profiles signed in with a
# Microsoft account, so it only gets Developer mode back via DevTools = 1
# (DevTools allowed except on force-installed extensions).
$Browsers = @(
    @{ Name = "Chrome"; Path = "HKLM\SOFTWARE\Policies\Google\Chrome";       Policies = [ordered]@{ IncognitoModeAvailability = 1 } }
    @{ Name = "Edge";   Path = "HKLM\SOFTWARE\Policies\Microsoft\Edge";      Policies = [ordered]@{ InPrivateModeAvailability = 1; DeveloperToolsAvailability = 1 } }
    @{ Name = "Brave";  Path = "HKLM\SOFTWARE\Policies\BraveSoftware\Brave"; Policies = [ordered]@{ IncognitoModeAvailability = 1; TorDisabled = 1 } }
)

# Only the first backup is kept: it is the pre-test state unset restores.
function Save-PolicyBackup($browser, $backupDir) {
    $regFile = Join-Path $backupDir "$($browser.Name).reg"
    $absentFile = Join-Path $backupDir "$($browser.Name).absent"
    if ((Test-Path $regFile) -or (Test-Path $absentFile)) { return }
    if (Test-Path "Registry::$($browser.Path)") {
        reg export $browser.Path $regFile /y | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "Could not back up $($browser.Path); nothing has been changed." }
    } else {
        New-Item -ItemType File -Path $absentFile | Out-Null
    }
}

function Restore-PolicyKey($browser, $backupDir) {
    $regFile = Join-Path $backupDir "$($browser.Name).reg"
    $absentFile = Join-Path $backupDir "$($browser.Name).absent"
    if (-not (Test-Path $regFile) -and -not (Test-Path $absentFile)) { return $false }
    if (Test-Path "Registry::$($browser.Path)") {
        Remove-Item -Path "Registry::$($browser.Path)" -Recurse -Force
    }
    if (Test-Path $regFile) {
        # reg import reports success on stderr, so its exit code is the only reliable signal.
        reg import $regFile 2>$null | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "Could not restore $($browser.Path); its backup is still at $regFile." }
    }
    return $true
}

# New-Item -Force on an existing registry key deletes the key and everything
# under it, hence the Test-Path guard before every New-Item here.
function Add-TestPolicies($browser) {
    $key = "Registry::$($browser.Path)"
    if (-not (Test-Path $key)) { New-Item -Path $key -Force | Out-Null }

    foreach ($policySet in @($CommonPolicies, $browser.Policies)) {
        foreach ($p in $policySet.GetEnumerator()) {
            $type = if ($p.Value -is [string]) { "String" } else { "DWord" }
            New-ItemProperty -Path $key -Name $p.Key -PropertyType $type -Value $p.Value -Force | Out-Null
        }
    }

    $list = "$key\URLBlocklist"
    if (-not (Test-Path $list)) { New-Item -Path $list | Out-Null }
    $taken = @((Get-Item -Path $list).Property)
    $n = 0
    foreach ($url in $TestBlocklist) {
        do { $n++ } while ($taken -contains "$n")
        New-ItemProperty -Path $list -Name "$n" -PropertyType String -Value $url | Out-Null
    }
}
