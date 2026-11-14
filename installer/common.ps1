# Shared by install.ps1 and uninstall.ps1: names, places and a few helpers.

$ProductName = "Smart Study Distraction Blocker"
# These match the code: AddWindowsService and EventLogSettings in the
# service's Program.cs, IpcProtocol.PipeName, and the extension's IDs.
$ServiceName = "SmartStudyBlockerService"
$EventSource = $ServiceName
$PipeName = "SmartStudyBlocker.Control"
$HostName = "com.smartstudyblocker.host"
# The extensions allowed to talk to the service: the unpacked copy, whose ID
# the manifest key pins (Chrome, and Edge while developing), and the copy Edge
# installs from Edge Add-ons (BrowserPolicies.EdgeStoreExtension), and the
# Chrome Web Store copy.
$ExtensionIds = @("dkbfaenfbikncbcgecpekmpgcemijgob", "jfaoannlngkcfncopcnofjiooamhfffm", "fgflnbhgcppnodpggmbeomdpciebgaln")

# Program Files: only administrators can change what's installed there.
$InstallDir = Join-Path $env:ProgramFiles $ProductName
$ServiceExe = Join-Path $InstallDir "service\StudyBlocker.Service.exe"
$AppExe = Join-Path $InstallDir "app\SmartStudyDistractionBlocker.exe"
$HostExe = Join-Path $InstallDir "native-host\StudyBlocker.NativeHost.exe"
$HostManifest = Join-Path $InstallDir "native-host\$HostName.json"
# Where the service leaves the pairing offer for LockIn (LinkProtocol.OfferFilePath):
# inside Program Files, so every user can read it and only administrators and
# the service can write it.
$LinkDir = Join-Path $InstallDir "link"
# The service's own data (lock, rules, channel lists, the LockIn link), ACL'd
# to SYSTEM and administrators. Kept on uninstall unless asked otherwise.
$SettingsDir = Join-Path $env:ProgramData "SmartStudyBlocker"
# Its entry in Windows' "Installed apps", under HKLM.
$UninstallKey = "SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\SmartStudyDistractionBlocker"

$StartMenuShortcut = Join-Path ([Environment]::GetFolderPath("CommonPrograms")) "$ProductName.lnk"
$DesktopShortcut = Join-Path ([Environment]::GetFolderPath("CommonDesktopDirectory")) "$ProductName.lnk"

# Where Chrome, Edge and Brave look for native-messaging hosts, under HKLM
# (every user) and HKCU (one user, checked first).
$NativeHostKeys = @(
    "SOFTWARE\Google\Chrome\NativeMessagingHosts\$HostName",
    "SOFTWARE\Microsoft\Edge\NativeMessagingHosts\$HostName",
    "SOFTWARE\BraveSoftware\Brave-Browser\NativeMessagingHosts\$HostName"
)

# A 32-bit PowerShell (the one Windows gives a 32-bit program, such as the
# setup .exe) takes Program Files (x86) for Program Files and writes to the
# 32-bit half of the registry, so everything would land where the 64-bit
# service, the browsers and LockIn don't look. Starts the calling script again
# in the 64-bit PowerShell and ends this copy with its exit code. Returns
# straight away when already 64-bit.
function Assert-64Bit([string]$ScriptPath, [string[]]$Arguments = @()) {
    if ([Environment]::Is64BitProcess -or -not [Environment]::Is64BitOperatingSystem) { return }
    & (Join-Path $env:WINDIR "Sysnative\WindowsPowerShell\v1.0\powershell.exe") -NoProfile -ExecutionPolicy Bypass -File $ScriptPath @Arguments
    exit $LASTEXITCODE
}

# Removes what the first 0.3.2 setup left: it ran the 32-bit PowerShell (see
# Assert-64Bit), so it installed into Program Files (x86) and registered in the
# 32-bit half of the registry. Chrome reads that half first, so its
# native-host entries would hide the real ones once the folder they name is
# gone. Run once the service runs from the real folder: until then the copy
# is what an install that fails halfway falls back to.
function Remove-32BitCopy {
    # Only a 64-bit PowerShell sees both halves, and there they can't be ours.
    if (-not [Environment]::Is64BitProcess) { return }
    foreach ($key in @($UninstallKey) + $NativeHostKeys) {
        $stray = "HKLM:\" + ($key -replace '^SOFTWARE\\', 'SOFTWARE\WOW6432Node\')
        if (Test-Path $stray) {
            Remove-Item $stray -Recurse -Force
            Write-Host "  removed $stray"
        }
    }
    $strayDir = Join-Path ${env:ProgramFiles(x86)} $ProductName
    if (Test-Path $strayDir) {
        # A browser may have started the old native host again meanwhile; with
        # the entries above gone, the next one it starts is the real one.
        Get-Process StudyBlocker.NativeHost -ErrorAction SilentlyContinue |
            Where-Object { $_.Path -like "$strayDir\*" } | Stop-Process -Force
        # Nothing uses it any more, so one that won't go isn't worth failing for.
        try {
            Remove-Folder $strayDir
            Write-Host "  removed $strayDir"
        } catch {
            Write-Host "  couldn't remove $strayDir, which nothing uses any more: $($_.Exception.Message)" -ForegroundColor Yellow
        }
    }
}

function Test-Admin {
    $principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

# Starts the calling script again as administrator (the one Windows prompt)
# and ends this copy. Returns straight away when already elevated.
function Assert-Elevated([string]$ScriptPath, [string[]]$Arguments = @()) {
    if (Test-Admin) { return }
    $argList = @("-NoProfile", "-ExecutionPolicy", "Bypass", "-File", "`"$ScriptPath`"") + $Arguments
    try {
        Start-Process powershell.exe -Verb RunAs -ArgumentList $argList
    } catch {
        Write-Host "Administrator rights weren't given, so nothing was changed." -ForegroundColor Yellow
        Read-Host "Press Enter to close"
    }
    exit
}

function Write-Step([string]$Text) {
    Write-Host ""
    Write-Host $Text -ForegroundColor Cyan
}

# Sends one request to the service over its pipe, the same way the app
# does, and returns the reply ({Ok, Error, Payload}), or $null when the
# service isn't there.
function Invoke-Service([string]$Type, [int]$TimeoutMs = 3000) {
    $pipe = New-Object System.IO.Pipes.NamedPipeClientStream(".", $PipeName,
        [System.IO.Pipes.PipeDirection]::InOut, [System.IO.Pipes.PipeOptions]::None,
        [System.Security.Principal.TokenImpersonationLevel]::Identification)
    try {
        $pipe.Connect($TimeoutMs)
        $utf8 = New-Object System.Text.UTF8Encoding($false)
        $writer = New-Object System.IO.StreamWriter($pipe, $utf8)
        $writer.AutoFlush = $true
        $reader = New-Object System.IO.StreamReader($pipe, $utf8)
        $writer.WriteLine((@{ Type = $Type } | ConvertTo-Json -Compress))
        $line = $reader.ReadLine()
        if ($line) { return $line | ConvertFrom-Json }
        return $null
    } catch {
        return $null
    } finally {
        $pipe.Dispose()
    }
}

# Lets any signed-in user START the service, and nothing more, so LockIn can
# bring it back if it was stopped. Stopping, pausing and reconfiguring it stay
# with administrators: Authenticated Users get what signed-in users already
# have on a service (query status/config, enumerate dependents, interrogate,
# user-defined controls, read the descriptor) plus start.
function Grant-ServiceStart([string]$Name) {
    $current = ((& sc.exe sdshow $Name) -join "").Trim()
    if ($LASTEXITCODE -ne 0 -or $current -notmatch '^D:') {
        throw "Couldn't read the security descriptor of the service ($current)."
    }
    # A security descriptor is a DACL, then optionally a SACL starting at "S:".
    $saclAt = $current.IndexOf("S:")
    $dacl = if ($saclAt -ge 0) { $current.Substring(0, $saclAt) } else { $current }
    $sacl = if ($saclAt -ge 0) { $current.Substring($saclAt) } else { "" }
    $ace = "(A;;CCLCSWRPLOCRRC;;;AU)"
    if ($dacl -match '\(A;;[A-Z]+;;;AU\)') {
        $dacl = [regex]::Replace($dacl, '\(A;;[A-Z]+;;;AU\)', $ace)
    } else {
        $dacl += $ace
    }
    & sc.exe sdset $Name "$dacl$sacl" | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "Couldn't let signed-in users start the service." }
}

# Stops everything that runs from the installed files or could fight over
# them: the installed service, a copy started with `dotnet run` from an
# earlier milestone, the app (old and new names) and any native host a
# browser has open at this moment. Waits until each has really exited, so
# its files can be replaced and the next copy can start.
function Stop-Everything {
    $service = Get-CimInstance Win32_Service -Filter "Name='$ServiceName'"
    if ($service -and $service.State -ne "Stopped") {
        Write-Host "  stopping the service"
        Stop-Service -Name $ServiceName -Force
        if ($service.ProcessId) { Wait-Process -Id $service.ProcessId -Timeout 30 -ErrorAction SilentlyContinue }
    }
    foreach ($process in @(Get-Process StudyBlocker.Service -ErrorAction SilentlyContinue)) {
        Write-Host "  stopping the service started from PowerShell (you can close that window)"
        Stop-Process -Id $process.Id -Force
        Wait-Process -Id $process.Id -Timeout 30 -ErrorAction SilentlyContinue
    }
    foreach ($process in @(Get-Process SmartStudyDistractionBlocker, StudyBlocker -ErrorAction SilentlyContinue)) {
        Write-Host "  closing the app"
        $process.CloseMainWindow() | Out-Null
        if (-not $process.WaitForExit(3000)) { Stop-Process -Id $process.Id -Force }
    }
    Get-Process StudyBlocker.NativeHost -ErrorAction SilentlyContinue | Stop-Process -Force
}

# Deletes a folder, retrying briefly in case something still has a file
# in it open.
function Remove-Folder([string]$Path) {
    for ($attempt = 1; (Test-Path $Path); $attempt++) {
        try {
            Remove-Item $Path -Recurse -Force
        } catch {
            if ($attempt -ge 10) { throw "Couldn't remove $Path. Is something still using it? ($($_.Exception.Message))" }
            Start-Sleep -Milliseconds 500
        }
    }
}
