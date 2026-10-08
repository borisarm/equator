<#
.SYNOPSIS
    Builds the Microsoft Store package: x64 and ARM64 MSIX packages combined into one bundle and wrapped in
    an .msixupload for Partner Center.

.DESCRIPTION
    Output: AppPackages\Equator_<version>.msixupload (upload this in Partner Center → Packages), plus the
    per-architecture .msix files and the .msixbundle. Packages are unsigned; the Store signs them.
    Requires the .NET 10 SDK and the Windows 10/11 SDK (makeappx.exe); Visual Studio is not needed.

.PARAMETER Version
    Package version (major.minor.build.0). The Store requires a higher version for every submission; the
    value is written into src\Equator.App\Package.appxmanifest. Defaults to the version in the manifest.

.PARAMETER Platforms
    Architectures to include (default: x64 and ARM64).

.EXAMPLE
    .\tools\package.ps1 -Version 1.0.1.0
#>
param(
    [string]$Version,
    [string[]]$Platforms = @("x64", "ARM64")
)
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$manifestPath = Join-Path $root "src\Equator.App\Package.appxmanifest"
$out = Join-Path $root "AppPackages"

# Version: the fourth field must be 0 for Store packages.
[xml]$manifest = Get-Content $manifestPath -Raw
$identity = $manifest.Package.Identity
if ($Version) {
    if ($Version -notmatch '^\d+\.\d+\.\d+\.0$') { throw "Version must look like 1.2.3.0 (the last field must be 0)." }
    $text = Get-Content $manifestPath -Raw
    $text = $text -replace '(<Identity[\s\S]*?Version=")[^"]+(")', "`${1}$Version`${2}"
    Set-Content $manifestPath $text -NoNewline -Encoding utf8
    $identity.Version = $Version
}
$Version = $identity.Version
Write-Host "Packaging $($identity.Name) $Version for $($Platforms -join ', ')"

$makeappx = Get-ChildItem "${env:ProgramFiles(x86)}\Windows Kits\10\bin\*\x64\makeappx.exe" -ErrorAction SilentlyContinue |
    Sort-Object FullName -Descending | Select-Object -First 1
if (-not $makeappx) { throw "makeappx.exe not found: install the Windows 10/11 SDK." }

if (Test-Path $out) { Remove-Item $out -Recurse -Force }
$staging = Join-Path $out "bundle"
New-Item -ItemType Directory -Force $staging | Out-Null

foreach ($platform in $Platforms) {
    Write-Host "Building $platform..."
    dotnet publish (Join-Path $root "src\Equator.App") -c Release "-p:Platform=$platform" -p:EquatorPackaged=true -nologo -v minimal
    if ($LASTEXITCODE -ne 0) { throw "Build failed for $platform." }
    $arch = $platform.ToLowerInvariant()
    $msix = Get-ChildItem $out -Recurse -Filter "Equator.App_$($Version)_$arch.msix" | Select-Object -First 1
    if (-not $msix) { throw "No package produced for $platform." }
    Copy-Item $msix.FullName (Join-Path $staging $msix.Name)
}

$bundle = Join-Path $out "Equator_$Version.msixbundle"
& $makeappx.FullName bundle /d $staging /p $bundle /bv $Version /o | Out-Host
if ($LASTEXITCODE -ne 0) { throw "makeappx bundle failed." }

# An .msixupload is a zip holding the bundle (symbol packages are optional).
$upload = Join-Path $out "Equator_$Version.msixupload"
$zip = "$upload.zip"
Compress-Archive -Path $bundle -DestinationPath $zip -Force
Move-Item $zip $upload -Force

Get-ChildItem $out -Filter "*.msix*" -File | ForEach-Object { "{0,-45} {1,8:N1} MB" -f $_.Name, ($_.Length / 1MB) }
Write-Host "`nUpload $upload in Partner Center (your app → Packages)."
