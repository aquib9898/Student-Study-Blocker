# Builds the Microsoft Store package: release\out\SmartStudyDistractionBlocker-<version>.msix
#
# Needs no administrator rights and no Windows SDK: Microsoft's packaging tools
# (makeappx, signtool) come from NuGet (msix\tools\tools.csproj). The package
# holds the same self-contained service, native host and app as the EXE setup
# (installer\build.ps1), in the same folders, plus msix\AppxManifest.template.xml
# and its tile images. Everything install.ps1 does besides copying files, the
# service does itself when it runs from the package (PackagedInstall.cs).
#
# For the Store, pass the identity Partner Center shows under Product
# management > Product identity, and upload the unsigned .msix (the Store signs
# it). To try it on a PC first, also pass a code-signing certificate whose
# subject is exactly -Publisher; the PC must trust it (Trusted People), and the
# EXE setup must be uninstalled first, since both register the same service.
param(
    [string]$IdentityName = "SmartStudyDistractionBlocker",
    [string]$Publisher = "CN=SmartStudyDistractionBlocker",
    [string]$PublisherDisplayName = "Smart Study Distraction Blocker",
    [string]$CertificatePfx = "",
    [string]$CertificatePassword = ""
)
$ErrorActionPreference = "Stop"
$repo = Split-Path $PSScriptRoot -Parent
$msix = Join-Path $PSScriptRoot "msix"
$csproj = [xml](Get-Content (Join-Path $repo "service\src\StudyBlocker.Service\StudyBlocker.Service.csproj"))
$version = ($csproj.Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1)
# MSIX versions have four parts, and the Store wants the last one to be 0.
$packageVersion = "$version.0"
$stage = Join-Path $env:TEMP "SmartStudyBlocker-msix-stage"
$layout = Join-Path $stage "layout"
$out = Join-Path $PSScriptRoot "out"
$package = Join-Path $out "SmartStudyDistractionBlocker-$version.msix"

Write-Host "Building the Microsoft Store package, version $packageVersion..." -ForegroundColor Cyan
Remove-Item $stage -Recurse -Force -ErrorAction SilentlyContinue
& (Join-Path $repo "installer\build.ps1") -OutDir $stage -SelfContained
New-Item -ItemType Directory -Path $layout | Out-Null
foreach ($part in "service", "native-host", "app") { Move-Item (Join-Path $stage $part) (Join-Path $layout $part) }
Copy-Item (Join-Path $msix "Assets") (Join-Path $layout "Assets") -Recurse

$manifest = (Get-Content (Join-Path $msix "AppxManifest.template.xml") -Raw).
    Replace("{{IdentityName}}", [Security.SecurityElement]::Escape($IdentityName)).
    Replace("{{Publisher}}", [Security.SecurityElement]::Escape($Publisher)).
    Replace("{{PublisherDisplayName}}", [Security.SecurityElement]::Escape($PublisherDisplayName)).
    Replace("{{Version}}", $packageVersion)
[IO.File]::WriteAllText((Join-Path $layout "AppxManifest.xml"), $manifest, (New-Object System.Text.UTF8Encoding($false)))

Write-Host "  fetching Microsoft's packaging tools"
& dotnet restore (Join-Path $msix "tools\tools.csproj") --nologo -v quiet
if ($LASTEXITCODE -ne 0) { throw "Couldn't download the packaging tools (Microsoft.Windows.SDK.BuildTools)." }
$nuget = ((& dotnet nuget locals global-packages --list) -replace '^global-packages:\s*', '').Trim()
$tools = Get-ChildItem (Join-Path $nuget "microsoft.windows.sdk.buildtools") -Recurse -Filter makeappx.exe |
    Where-Object { $_.Directory.Name -eq "x64" } | Sort-Object FullName -Descending | Select-Object -First 1 -ExpandProperty DirectoryName
if (-not $tools) { throw "makeappx.exe wasn't found in the NuGet cache ($nuget)." }

Write-Host "  packing"
New-Item -ItemType Directory -Path $out -Force | Out-Null
& (Join-Path $tools "makeappx.exe") pack /d $layout /p $package /o | Out-Null
if ($LASTEXITCODE -ne 0) { throw "makeappx couldn't pack the package (run it by hand on $layout to see why)." }

if ($CertificatePfx) {
    Write-Host "  signing for sideloading"
    & (Join-Path $tools "signtool.exe") sign /fd SHA256 /f $CertificatePfx /p $CertificatePassword $package | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "signtool couldn't sign the package with $CertificatePfx (its subject must be exactly $Publisher)." }
}
Remove-Item $stage -Recurse -Force -ErrorAction SilentlyContinue
$size = [math]::Round((Get-Item $package).Length / 1MB)
Write-Host "Done: $package ($size MB)$(if (-not $CertificatePfx) { ', unsigned: the Store signs it' })" -ForegroundColor Green
