# Builds the one-file installer: release\out\Study-Blocker-Setup-<version>.exe
#
# Needs no administrator rights. It publishes the service, the extension's
# native-messaging host and the app as self-contained programs (so the person
# installing needs nothing else, not even .NET), bundles them with the install
# scripts, and wraps it all in an NSIS setup program. NSIS comes from the copy
# electron-builder (LockIn's build) already downloaded, or from NSIS on the PATH.
$ErrorActionPreference = "Stop"
$repo = Split-Path $PSScriptRoot -Parent
$version = ([xml](Get-Content (Join-Path $repo "service\src\StudyBlocker.Service\StudyBlocker.Service.csproj"))).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
$stage = Join-Path $env:TEMP "SmartStudyBlocker-setup-stage"
$out = Join-Path $PSScriptRoot "out"
$exe = Join-Path $out "Study-Blocker-Setup-$version.exe"

$makensis = (Get-Command makensis.exe -ErrorAction SilentlyContinue).Source
if (-not $makensis) {
    $makensis = Get-ChildItem (Join-Path $env:LOCALAPPDATA "electron-builder\Cache\nsis*") -Recurse -Filter makensis.exe -ErrorAction SilentlyContinue |
        Where-Object { $_.Directory.Name -ne "Bin" } | Select-Object -First 1 -ExpandProperty FullName
}
if (-not $makensis) { throw "NSIS (makensis.exe) wasn't found. Install NSIS, or run LockIn's 'npm run dist' once so electron-builder downloads it." }

Write-Host "Building Study Blocker $version (self-contained)..." -ForegroundColor Cyan
Remove-Item $stage -Recurse -Force -ErrorAction SilentlyContinue
& (Join-Path $repo "installer\build.ps1") -OutDir $stage -SelfContained
Remove-Item (Join-Path $stage "work") -Recurse -Force -ErrorAction SilentlyContinue
$scripts = Join-Path $stage "installer"
New-Item -ItemType Directory -Path $scripts | Out-Null
foreach ($name in "common.ps1", "install.ps1", "uninstall.ps1", "build.ps1") { Copy-Item (Join-Path $repo "installer\$name") $scripts }

Write-Host "Packing the installer..." -ForegroundColor Cyan
New-Item -ItemType Directory -Path $out -Force | Out-Null
Remove-Item $exe -Force -ErrorAction SilentlyContinue
& $makensis /V2 "/DVERSION=$version" "/DSTAGE=$stage" "/DOUTFILE=$exe" "/DICON=$(Join-Path $repo 'app\StudyBlocker.App\Assets\logo.ico')" (Join-Path $PSScriptRoot "setup.nsi")
if ($LASTEXITCODE -ne 0) { throw "makensis failed." }
Remove-Item $stage -Recurse -Force
Write-Host ("Done: {0} ({1:N0} MB)" -f $exe, ((Get-Item $exe).Length / 1MB)) -ForegroundColor Green
