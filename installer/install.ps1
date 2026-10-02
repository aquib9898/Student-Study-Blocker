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

. (Join-Path $PSScriptRoot "common.ps1")
Assert-Elevated $PSCommandPath

$ErrorActionPreference = "Stop"
Start-Transcript -Path (Join-Path $PSScriptRoot "last-install.log") | Out-Null
try {
    Write-Host "Installing $ProductName" -ForegroundColor Cyan

    Write-Step "1/6  Building..."
    $build = Join-Path $env:TEMP "SmartStudyBlocker-build"
    & (Join-Path $PSScriptRoot "build.ps1") -OutDir $build

    Write-Step "2/6  Stopping what's running..."
    Stop-Everything

    Write-Step "3/6  Copying to $InstallDir..."
    New-Item -ItemType Directory -Path $InstallDir -Force | Out-Null
    foreach ($part in "service", "native-host", "app") {
        $destination = Join-Path $InstallDir $part
        Remove-Folder $destination
        Copy-Item (Join-Path $build $part) $destination -Recurse
    }
    Remove-Folder $build

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

    Write-Step "5/6  Connecting the browser extension..."
    $manifest = [ordered]@{
        name            = $HostName
        description     = "$ProductName native-messaging bridge to the service"
        path            = $HostExe
        type            = "stdio"
        allowed_origins = @("chrome-extension://$ExtensionId/")
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

    # Opened through Explorer so it runs as you, not as administrator.
    Start-Process explorer.exe -ArgumentList "`"$AppExe`""

    Write-Host ""
    Write-Host "Done. $ProductName is installed and running." -ForegroundColor Green
    Write-Host "  - The service starts by itself with Windows. No PowerShell needed any more."
    Write-Host "  - Open the app from the Start menu or the desktop shortcut."
    Write-Host "  - To update after a change, run Install.cmd again. To remove it, run Uninstall.cmd."
} catch {
    Write-Host ""
    Write-Host "Install failed: $($_.Exception.Message)" -ForegroundColor Red
    Write-Host "The full log is in $(Join-Path $PSScriptRoot 'last-install.log')."
    # Don't leave the PC unprotected because an update went wrong halfway.
    if ((Get-Service -Name $ServiceName -ErrorAction SilentlyContinue) -and (Test-Path $ServiceExe)) {
        try { Start-Service -Name $ServiceName; Write-Host "The installed service was started again." } catch { }
    }
} finally {
    Stop-Transcript | Out-Null
    Read-Host "Press Enter to close"
}
