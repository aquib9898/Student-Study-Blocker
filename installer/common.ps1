# Shared by install.ps1 and uninstall.ps1: names, places and a few helpers.

$ProductName = "Smart Study Distraction Blocker"
# These match the code: AddWindowsService and EventLogSettings in the
# service's Program.cs, IpcProtocol.PipeName, and the extension's manifest key.
$ServiceName = "SmartStudyBlockerService"
$EventSource = $ServiceName
$PipeName = "SmartStudyBlocker.Control"
$HostName = "com.smartstudyblocker.host"
$ExtensionId = "dkbfaenfbikncbcgecpekmpgcemijgob"

# Program Files: only administrators can change what's installed there.
$InstallDir = Join-Path $env:ProgramFiles $ProductName
$ServiceExe = Join-Path $InstallDir "service\StudyBlocker.Service.exe"
$AppExe = Join-Path $InstallDir "app\SmartStudyDistractionBlocker.exe"
$HostExe = Join-Path $InstallDir "native-host\StudyBlocker.NativeHost.exe"
$HostManifest = Join-Path $InstallDir "native-host\$HostName.json"
# The service's own data (lock, rules, channel lists), ACL'd to SYSTEM and
# administrators. Kept on uninstall unless asked otherwise.
$SettingsDir = Join-Path $env:ProgramData "SmartStudyBlocker"

$StartMenuShortcut = Join-Path ([Environment]::GetFolderPath("CommonPrograms")) "$ProductName.lnk"
$DesktopShortcut = Join-Path ([Environment]::GetFolderPath("CommonDesktopDirectory")) "$ProductName.lnk"

# Where Chrome, Edge and Brave look for native-messaging hosts, under HKLM
# (every user) and HKCU (one user, checked first).
$NativeHostKeys = @(
    "SOFTWARE\Google\Chrome\NativeMessagingHosts\$HostName",
    "SOFTWARE\Microsoft\Edge\NativeMessagingHosts\$HostName",
    "SOFTWARE\BraveSoftware\Brave-Browser\NativeMessagingHosts\$HostName"
)

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
