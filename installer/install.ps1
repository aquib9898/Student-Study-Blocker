# Installs Smart Study Distraction Blocker on this PC, or updates it: run it
# again after a code change. It asks for administrator rights once (the
# Windows prompt). After that the service starts by itself with Windows, and
# the app opens from the Start menu or the desktop without asking.
#
# What it does:
#   1. builds the service, the extension's native-messaging host and the app
#   2. stops what's running: the installed service, a copy started with
#      `dotnet run`, the app
#   3. copies them to C:\Program Files\Smart Study Distraction Blocker
#   4. registers the service to start with Windows, as SYSTEM, restarting by
#      itself if it ever stops unexpectedly
#   5. registers the native host for Chrome, Edge and Brave, for every user
#   6. adds Start menu and desktop shortcuts, starts the service and opens
#      the app
#
# Settings (the lock, rules, channel lists) live in C:\ProgramData and are
# kept as they are, so a lock that's running carries on through an update.
# So is the link to LockIn. If that link is ever lost (LockIn was reinstalled
# and forgot its secret), -ResetLink forgets it on this side too, and the
# service leaves a fresh pairing offer for LockIn to pick up. Only an
# administrator can do that, which is the point of it being here.
param([switch]$ResetLink, [string]$PrebuiltDir = "", [switch]$Quiet)

. (Join-Path $PSScriptRoot "common.ps1")
$forward = @()
if ($ResetLink) { $forward += "-ResetLink" }
if ($PrebuiltDir) { $forward += "-PrebuiltDir", $PrebuiltDir }
if ($Quiet) { $forward += "-Quiet" }
Assert-64Bit $PSCommandPath $forward
Assert-Elevated $PSCommandPath $(if ($ResetLink) { @("-ResetLink") } else { @() })

$ErrorActionPreference = "Stop"
$logPath = if ($Quiet) { Join-Path $env:TEMP "SmartStudyBlocker-install.log" } else { Join-Path $PSScriptRoot "last-install.log" }
Start-Transcript -Path $logPath | Out-Null
$failed = $false
try {
    Write-Host "Installing $ProductName" -ForegroundColor Cyan

    Write-Step "1/6  Building..."
    if ($PrebuiltDir) { Write-Host "  using the files that came with this installer" }
    $build = if ($PrebuiltDir) { $PrebuiltDir } else { Join-Path $env:TEMP "SmartStudyBlocker-build" }
    if (-not $PrebuiltDir) { & (Join-Path $PSScriptRoot "build.ps1") -OutDir $build }

    Write-Step "2/6  Stopping what's running..."
    Stop-Everything

    if ($ResetLink) {
        Write-Step "Forgetting the link to LockIn..."
        foreach ($name in "link.json", "link-offer.json", "linked-session.json") {
            Remove-Item (Join-Path $SettingsDir $name) -Force -ErrorAction SilentlyContinue
        }
        Remove-Item (Join-Path $LinkDir "pairing-offer.json") -Force -ErrorAction SilentlyContinue
    }

    Write-Step "3/6  Copying to $InstallDir..."
    New-Item -ItemType Directory -Path $InstallDir -Force | Out-Null
    # Not one of the folders replaced below: it holds the pairing offer, which
    # must stay put through an update.
    New-Item -ItemType Directory -Path $LinkDir -Force | Out-Null
    foreach ($part in "service", "native-host", "app") {
        $destination = Join-Path $InstallDir $part
        Remove-Folder $destination
        Copy-Item (Join-Path $build $part) $destination -Recurse
    }
    if (-not $PrebuiltDir) { Remove-Folder $build }

    # The scripts that remove it again, kept next to it so Windows' own
    # "Installed apps" list can offer Uninstall (registered below).
    $scriptsDir = Join-Path $InstallDir "installer"
    Remove-Folder $scriptsDir
    New-Item -ItemType Directory -Path $scriptsDir -Force | Out-Null
    foreach ($name in "common.ps1", "uninstall.ps1") { Copy-Item (Join-Path $PSScriptRoot $name) $scriptsDir }
    $version = ((Get-Item $ServiceExe).VersionInfo.ProductVersion -split '\+')[0]
    $uninstallKey = "HKLM:\$UninstallKey"
    New-Item -Path $uninstallKey -Force | Out-Null
    $entries = @{
        DisplayName     = $ProductName
        DisplayVersion  = $version
        Publisher       = $ProductName
        DisplayIcon     = $AppExe
        InstallLocation = $InstallDir
        UninstallString = "powershell.exe -NoProfile -ExecutionPolicy Bypass -File `"$(Join-Path $scriptsDir 'uninstall.ps1')`""
    }
    foreach ($key in $entries.Keys) { Set-ItemProperty -Path $uninstallKey -Name $key -Value $entries[$key] }
    Set-ItemProperty -Path $uninstallKey -Name NoModify -Value 1 -Type DWord
    Set-ItemProperty -Path $uninstallKey -Name NoRepair -Value 1 -Type DWord

    Write-Step "4/6  Registering the service..."
    if (-not [System.Diagnostics.EventLog]::SourceExists($EventSource)) {
        New-EventLog -LogName Application -Source $EventSource
    }
    $description = "Enforces $ProductName's blocked sites, YouTube rules and locks. The app only asks it for changes."
    $existing = Get-CimInstance Win32_Service -Filter "Name='$ServiceName'"
    if ($existing) {
        if ($existing.PathName -ne "`"$ServiceExe`"") {
            Invoke-CimMethod -InputObject $existing -MethodName Change -Arguments @{ PathName = "`"$ServiceExe`"" } | Out-Null
        }
        Set-Service -Name $ServiceName -DisplayName $ProductName -Description $description -StartupType Automatic
    } else {
        New-Service -Name $ServiceName -BinaryPathName "`"$ServiceExe`"" -DisplayName $ProductName `
            -Description $description -StartupType Automatic | Out-Null
    }
    # Restarted 5 s after stopping unexpectedly (a crash, or its process being
    # ended), every time. Only administrators can stop it on purpose.
    & sc.exe failure $ServiceName reset= 86400 actions= restart/5000/restart/5000/restart/5000 | Out-Null
    & sc.exe failureflag $ServiceName 1 | Out-Null
    # Signed-in users may start it (LockIn does, if it was stopped) but not
    # stop it.
    Grant-ServiceStart $ServiceName

    Write-Step "5/6  Connecting the browser extension..."
    $manifest = [ordered]@{
        name            = $HostName
        description     = "$ProductName native-messaging bridge to the service"
        path            = $HostExe
        type            = "stdio"
        allowed_origins = @($ExtensionIds | ForEach-Object { "chrome-extension://$_/" })
    }
    # UTF-8 without a byte-order mark, which Chrome requires.
    [System.IO.File]::WriteAllText($HostManifest, ($manifest | ConvertTo-Json -Depth 5), (New-Object System.Text.UTF8Encoding($false)))
    foreach ($key in $NativeHostKeys) {
        New-Item -Path "HKLM:\$key" -Force | Out-Null
        Set-Item -Path "HKLM:\$key" -Value $HostManifest
    }
    # The per-user registration from development (extension\native-host\
    # install-host.ps1) would win over the one above, so it goes.
    foreach ($key in $NativeHostKeys) {
        if (Test-Path "HKCU:\$key") {
            Remove-Item "HKCU:\$key" -Force
            Write-Host "  removed the development registration HKCU:\$key"
        }
    }

    Write-Step "6/6  Shortcuts, then starting..."
    $shell = New-Object -ComObject WScript.Shell
    foreach ($shortcutPath in $StartMenuShortcut, $DesktopShortcut) {
        $shortcut = $shell.CreateShortcut($shortcutPath)
        $shortcut.TargetPath = $AppExe
        $shortcut.WorkingDirectory = Split-Path $AppExe
        $shortcut.Description = $ProductName
        $shortcut.Save()
    }

    Start-Service -Name $ServiceName
    $answering = $false
    for ($i = 0; $i -lt 20 -and -not $answering; $i++) {
        Start-Sleep -Milliseconds 500
        $reply = Invoke-Service "ping"
        $answering = $null -ne $reply -and $reply.Ok
    }
    if (-not $answering) {
        throw "The service is installed but isn't answering. Event Viewer > Windows Logs > Application, source '$EventSource', should say why."
    }
    Remove-32BitCopy

    # Opened through Explorer so it runs as you, not as administrator.
    Start-Process explorer.exe -ArgumentList "`"$AppExe`""

    Write-Host ""
    Write-Host "Done. $ProductName is installed and running." -ForegroundColor Green
    Write-Host "  - The service starts by itself with Windows. No PowerShell needed any more."
    Write-Host "  - Open the app from the Start menu or the desktop shortcut."
    Write-Host "  - To link LockIn: in LockIn, open Settings > Link Study Blocker and click Link."
    Write-Host "  - Chrome, Edge and Brave now install the extension by themselves. If it is not there a minute or two after restarting them, check the extensions page."
    Write-Host "  - To update after a change, run Install.cmd again. To remove it, run Uninstall.cmd."
} catch {
    Write-Host ""
    Write-Host "Install failed: $($_.Exception.Message)" -ForegroundColor Red
    Write-Host "The full log is in $logPath."
    $failed = $true
    # Don't leave the PC unprotected because an update went wrong halfway.
    if ((Get-Service -Name $ServiceName -ErrorAction SilentlyContinue) -and (Test-Path $ServiceExe)) {
        try { Start-Service -Name $ServiceName; Write-Host "The installed service was started again." } catch { }
    }
} finally {
    Stop-Transcript | Out-Null
    if (-not $Quiet) { Read-Host "Press Enter to close" }
}
if ($failed) { exit 1 }
