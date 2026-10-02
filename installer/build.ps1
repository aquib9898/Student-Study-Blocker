# Builds what the installer puts in place: the service, the extension's
# native-messaging host and the app, for 64-bit Windows, using the .NET 10
# runtime already on this PC. Needs no administrator rights; install.ps1
# runs it first. Build files go under -OutDir, never into the repo's own
# bin/obj folders.
param([Parameter(Mandatory = $true)] [string]$OutDir)

$ErrorActionPreference = "Stop"
$repo = Split-Path $PSScriptRoot -Parent
$work = Join-Path $OutDir "work"
$projects = [ordered]@{
    "service"     = "service\src\StudyBlocker.Service\StudyBlocker.Service.csproj"
    "native-host" = "service\src\StudyBlocker.NativeHost\StudyBlocker.NativeHost.csproj"
    "app"         = "app\StudyBlocker.App\StudyBlocker.App.csproj"
}

foreach ($part in $projects.Keys) {
    $target = Join-Path $OutDir $part
    if (Test-Path $target) { Remove-Item $target -Recurse -Force }
    Write-Host "  building the $part"
    & dotnet publish (Join-Path $repo $projects[$part]) -c Release -r win-x64 --self-contained false `
        -o $target --artifacts-path $work --nologo -v quiet
    if ($LASTEXITCODE -ne 0) { throw "Building the $part failed (see the messages above)." }
}
