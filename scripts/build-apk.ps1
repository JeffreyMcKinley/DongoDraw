<#
.SYNOPSIS
  Builds an installable DongoDraw APK and copies it to artifacts\.

.DESCRIPTION
  Sideload flow (no emulator, no MSBuild -t:Run):

    1. `dotnet publish -c Release -p:AndroidPackageFormat=apk` -> a signed, self-contained APK
       (Release publish defaults to .aab, which `adb install` cannot take, hence the explicit format)
    2. copies the signed APK to artifacts\DongoDraw-<version>-<config>.apk and writes a
       .json manifest beside it (version, versionCode, commit, UTC time, SHA-256)
    3. optionally `adb install -r` it onto an attached phone (-Install)

  Versioning: the semantic version comes from version.props (see that file and
  scripts\bump-version.ps1); this script never invents one. It does own the build number: each run
  consumes the next one (1.2.3+4 -> 1.2.3+5) and writes it back to version.props after a successful
  publish, so two APKs built from the same commit never claim the same versionCode and a phone
  accepts the second as an upgrade. A semantic bump resets the count to 0 — that reset is what keeps
  the field under its 99 ceiling; at 99 this script stops and tells you to bump.

  Opt out with -NoBump (build exactly what version.props says) or -BuildNumber N (pin one for this
  build without editing the file — use that from CI). Every generated APK is therefore identifiable
  from its filename alone, and the manifest ties it back to the exact commit it was built from.

  Signing: with no -KeyStore the APK is signed with the local Android debug key. That installs on a
  normal phone (USB / "install unknown apps") but is NOT suitable for Play Store upload. Pass
  -KeyStore/-KeyAlias/-KeyPass for a real release key.

  Debug config is supported (-Configuration Debug) but forces EmbedAssembliesIntoApk=true; a Debug
  APK built without it uses Fast Deployment and crashes at launch with "No assemblies found" when
  installed via adb.

  Android SDK + JDK 17 paths come from Directory.Build.props; they are passed explicitly here too so
  the script works from a shell with a different JAVA_HOME.

.EXAMPLE
  pwsh scripts\build-apk.ps1
  pwsh scripts\build-apk.ps1 -Install                       # build, then adb install to the phone
  pwsh scripts\build-apk.ps1 -Configuration Debug -Install
  pwsh scripts\build-apk.ps1 -NoBump                         # build version.props as it stands
  pwsh scripts\build-apk.ps1 -BuildNumber 7                  # 1.2.3 rebuilt as 1.2.3.7, file untouched
  pwsh scripts\build-apk.ps1 -KeyStore C:\keys\fd.keystore -KeyAlias fd -KeyPass hunter2
#>
[CmdletBinding()]
param(
    [ValidateSet("Release", "Debug")]
    [string]$Configuration = "Release",
    [string]$Jdk = $env:JavaSdkDirectory,
    [string]$Sdk = $env:AndroidSdkDirectory,
    [string]$OutDir = "",
    # -1 means "consume the next build number and write it back"; 0-99 pins one for this build only
    # and leaves version.props untouched (see -NoBump).
    [ValidateRange(-1, 99)]
    [int]$BuildNumber = -1,
    [switch]$NoBump,
    [switch]$Install,
    [string]$Target = "",
    [string]$KeyStore = "",
    [string]$KeyAlias = "",
    [string]$KeyPass = ""
)

# adb/dotnet write progress to stderr, which PowerShell surfaces as NativeCommandError; 'Stop' would
# abort on harmless output. Native steps are checked explicitly via $LASTEXITCODE.
$ErrorActionPreference = "Continue"
$repoRoot = Split-Path -Parent $PSScriptRoot
. "$PSScriptRoot\version-lib.ps1"

function Die($msg) { Write-Host $msg -ForegroundColor Red; exit 1 }

# Version first: the numbers name the output file and go into the manifest, and a malformed
# version.props (or a full build-number field) should stop the build before a 40s publish, not
# after it.
$version = Get-FdVersion -RepoRoot $repoRoot

# Which build number this APK gets, and whether version.props learns about it:
#   default          the next one, written back after a successful publish, so no two APKs of the
#                    same semantic version ever share a versionCode
#   -BuildNumber N   pinned for this build only; CI stamps a rebuild without touching the file
#   -NoBump          exactly what version.props says now (rebuilding an artifact byte-for-byte)
$persistBuildNumber = $false
if ($BuildNumber -lt 0 -and -not $NoBump) {
    try { $BuildNumber = Get-FdNextBuildNumber -RepoRoot $repoRoot } catch { Die $_.Exception.Message }
    $persistBuildNumber = $true
}

if ($BuildNumber -ge 0 -and $BuildNumber -ne $version.Build) {
    $version.Build = $BuildNumber
    $version.Full  = if ($BuildNumber -eq 0) { $version.Semantic } else { "$($version.Semantic).$BuildNumber" }
    $version.Code  = $version.Major * 1000000 + $version.Minor * 10000 + $version.Patch * 100 + $BuildNumber
}

if (-not $Jdk) { $Jdk = "C:\Program Files\Microsoft\jdk-17.0.19.10-hotspot" }
if (-not $Sdk) { $Sdk = Join-Path $env:LOCALAPPDATA "Android\Sdk" }
if (-not $OutDir) { $OutDir = Join-Path $repoRoot "artifacts" }

$env:ANDROID_HOME     = $Sdk
$env:ANDROID_SDK_ROOT = $Sdk
$env:JAVA_HOME        = $Jdk

$project = Join-Path $repoRoot "DongoDraw.csproj"
$tfm     = "net9.0-android"

# 1. Publish ---------------------------------------------------------------------------------------
$publishArgs = @(
    $project,
    "-c", $Configuration,
    "-f", $tfm,
    "--nologo",
    "-p:AndroidPackageFormat=apk",
    "-p:JavaSdkDirectory=$Jdk",
    "-p:AndroidSdkDirectory=$Sdk"
)
if ($Configuration -eq "Debug") { $publishArgs += "-p:EmbedAssembliesIntoApk=true" }
# A global property beats version.props, so the APK's own versionCode/versionName match the
# filename this script is about to write.
if ($BuildNumber -ge 0) { $publishArgs += "-p:FdBuildNumber=$BuildNumber" }
if ($KeyStore) {
    if (-not (Test-Path $KeyStore)) { Die "Keystore not found: $KeyStore" }
    if (-not $KeyAlias) { Die "-KeyAlias is required with -KeyStore." }
    $publishArgs += @(
        "-p:AndroidKeyStore=true",
        "-p:AndroidSigningKeyStore=$KeyStore",
        "-p:AndroidSigningKeyAlias=$KeyAlias",
        "-p:AndroidSigningKeyPass=$KeyPass",
        "-p:AndroidSigningStorePass=$KeyPass"
    )
}

Write-Host "Publishing $Configuration APK $($version.Full) (versionCode $($version.Code))..." -ForegroundColor Cyan
& dotnet publish @publishArgs
if ($LASTEXITCODE -ne 0) { Die "Publish failed." }

# Only now: a failed publish must not consume a build number, or the numbers gap for nothing.
if ($persistBuildNumber) {
    Set-FdBuildNumber -Build $version.Build -RepoRoot $repoRoot
    Write-Host "version.props: FdBuildNumber -> $($version.Build)" -ForegroundColor DarkGray
}

# 2. Locate + copy ---------------------------------------------------------------------------------
$publishDir = Join-Path $repoRoot "bin\$Configuration\$tfm\publish"
# Prefer the *-Signed.apk; only that one is zipaligned + signed and installable.
$apk = Get-ChildItem -Path $publishDir -Filter "*-Signed.apk" -ErrorAction SilentlyContinue |
       Sort-Object LastWriteTime -Descending | Select-Object -First 1
if (-not $apk) {
    $apk = Get-ChildItem -Path $publishDir -Filter "*.apk" -ErrorAction SilentlyContinue |
           Sort-Object LastWriteTime -Descending | Select-Object -First 1
}
if (-not $apk) { Die "No APK found in $publishDir" }

if (-not (Test-Path $OutDir)) { New-Item -ItemType Directory -Path $OutDir | Out-Null }
$stem = "DongoDraw-$($version.Full)-$($Configuration.ToLower())"
$dest = Join-Path $OutDir "$stem.apk"
Copy-Item $apk.FullName $dest -Force

# 2b. Manifest -------------------------------------------------------------------------------------
# The filename carries the version; this carries everything needed to answer "which build is on this
# phone, and what was in it" months later — commit, dirty flag, and a hash of the exact bytes.
$sha = (Get-FileHash -Path $dest -Algorithm SHA256).Hash.ToLower()
$commit = & git -C $repoRoot rev-parse --short HEAD 2>$null
if ($LASTEXITCODE -ne 0) { $commit = "unknown" }
$dirty = $false
if ($commit -ne "unknown") {
    $status = & git -C $repoRoot status --porcelain 2>$null
    $dirty = -not [string]::IsNullOrWhiteSpace(($status -join ""))
}

$manifest = [ordered]@{
    version       = $version.Full
    semantic      = $version.Semantic
    versionCode   = $version.Code
    configuration = $Configuration
    applicationId = ([xml](Get-Content -Raw $project)).Project.PropertyGroup.ApplicationId | Where-Object { $_ } | Select-Object -First 1
    apk           = Split-Path -Leaf $dest
    sha256        = $sha
    commit        = $commit
    dirtyWorktree = $dirty
    signedWith    = if ($KeyStore) { Split-Path -Leaf $KeyStore } else { "android debug key" }
    builtUtc      = (Get-Date).ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ")
}
$manifestPath = Join-Path $OutDir "$stem.json"
$manifest | ConvertTo-Json | Set-Content -Path $manifestPath -Encoding utf8

$sizeMb = [math]::Round((Get-Item $dest).Length / 1MB, 1)
Write-Host "APK: $dest ($sizeMb MB)" -ForegroundColor Green
Write-Host "     version $($version.Full)  versionCode $($version.Code)  commit $commit$(if ($dirty) { ' (dirty)' })" -ForegroundColor Green
Write-Host "     manifest $manifestPath" -ForegroundColor DarkGray

# 3. Optional install ------------------------------------------------------------------------------
if ($Install) {
    $adb = Join-Path $Sdk "platform-tools\adb.exe"
    if (-not (Test-Path $adb)) { Die "adb not found at $adb" }
    $adbArgs = @()
    if ($Target) { $adbArgs += @("-s", $Target) }
    $adbArgs += @("install", "-r", $dest)

    Write-Host "Installing to device..." -ForegroundColor Cyan
    & $adb @adbArgs
    if ($LASTEXITCODE -ne 0) { Die "adb install failed." }
    Write-Host "Installed." -ForegroundColor Green
}
