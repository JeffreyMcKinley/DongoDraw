<#
.SYNOPSIS
  Deploys and launches the DongoDraw app on the DongoDraw_Pixel emulator.

.DESCRIPTION
  Manual "run the app" flow:

    1. boots the DongoDraw_Pixel emulator if no device is attached, and waits for boot
    2. deploys + launches via `dotnet build -t:Run` (self-installs, no fragile adb install)

  Directory.Build.props supplies the Android SDK + JDK 17 paths, so no -p: overrides are needed
  for a plain build. This script still exports ANDROID_HOME / JAVA_HOME so the emulator + adb
  tooling resolve, and passes the paths explicitly as a belt-and-suspenders override.

.EXAMPLE
  pwsh scripts\run-app.ps1
  pwsh scripts\run-app.ps1 -Target emulator-5554     # force a device when several are attached
#>
[CmdletBinding()]
param(
    [string]$Jdk = $env:JavaSdkDirectory,
    [string]$Sdk = $env:AndroidSdkDirectory,
    [string]$Avd = "DongoDraw_Pixel",
    [string]$Target = ""
)

# NOTE: deliberately NOT 'Stop'. adb/emulator log progress to stderr, which PowerShell wraps as
# NativeCommandError; with 'Stop' that would abort on harmless output. Critical native steps are
# checked explicitly via $LASTEXITCODE / Die below.
$ErrorActionPreference = "Continue"
$repoRoot = Split-Path -Parent $PSScriptRoot

function Die($msg) { Write-Host $msg -ForegroundColor Red; exit 1 }

if (-not $Jdk) { $Jdk = "C:\Program Files\Microsoft\jdk-17.0.19.10-hotspot" }
if (-not $Sdk) { $Sdk = Join-Path $env:LOCALAPPDATA "Android\Sdk" }

$adb      = Join-Path $Sdk "platform-tools\adb.exe"
$emulator = Join-Path $Sdk "emulator\emulator.exe"

$env:ANDROID_HOME     = $Sdk
$env:ANDROID_SDK_ROOT = $Sdk
$env:JAVA_HOME        = $Jdk

function Wait-For([scriptblock]$Condition, [int]$TimeoutSec, [string]$What) {
    $deadline = (Get-Date).AddSeconds($TimeoutSec)
    while ((Get-Date) -lt $deadline) {
        if (& $Condition) { return }
        Start-Sleep -Seconds 2
    }
    Die "Timed out waiting for: $What"
}

# 1. Emulator -------------------------------------------------------------------------------------
$attached = (& $adb devices) | Select-String "device$"
if (-not $attached) {
    Write-Host "Booting emulator $Avd..." -ForegroundColor Cyan
    Start-Process -FilePath $emulator -ArgumentList "-avd", $Avd
    & $adb wait-for-device
    Wait-For { (& $adb shell getprop sys.boot_completed 2>$null).Trim() -eq "1" } 180 "emulator boot"
}
Write-Host "Device ready." -ForegroundColor Green

# 2. Deploy + launch ------------------------------------------------------------------------------
# -t:Run deploys via Fast Deployment and starts the activity. A plain `adb install` of a Debug APK
# crashes at launch ("No assemblies found"), so always run through the MSBuild target.
Write-Host "Deploying to device..." -ForegroundColor Cyan
$buildArgs = @(
    (Join-Path $repoRoot "DongoDraw.csproj"),
    "-t:Run", "-c", "Debug", "--nologo",
    "-p:JavaSdkDirectory=$Jdk", "-p:AndroidSdkDirectory=$Sdk"
)
if ($Target) { $buildArgs += "-p:AdbTarget=-s $Target" }

& dotnet build @buildArgs
if ($LASTEXITCODE -ne 0) { Die "Deploy failed." }
Write-Host "App launched on device." -ForegroundColor Green
