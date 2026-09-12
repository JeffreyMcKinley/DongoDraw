<#
.SYNOPSIS
  Runs the DongoDraw Appium end-to-end UI tests (DongoDraw.UITests).

.DESCRIPTION
  These tests need infrastructure that a normal `nx test` deliberately skips. This script wires it
  all up, runs the suite with RUN_APPIUM=1, and tears the server down afterwards:

    1. installs Appium + the uiautomator2 driver if missing (global npm)
    2. boots the DongoDraw_Pixel emulator if no device is attached
    3. builds + installs the signed APK
    4. starts an Appium server, waits for it, runs the tests, stops the server

  Prereqs (see repo memory): JDK 17 + Android SDK. Override paths with the params below or the
  matching environment variables.

  Two AVDs are in play. DongoDraw_Pixel (phone) is the default. DongoDraw_Fold is a
  "7.6in Foldable" (1768x2208 @420dpi = 673dp wide unfolded), which is the geometry the Galaxy Z
  Fold7 rail bug showed up on: the player rail becomes a fixed-width column beside the pose there
  without any rotation. Run it with -Avd DongoDraw_Fold; -CreateAvd creates whichever AVD was
  named if it does not exist yet.
#>
[CmdletBinding()]
param(
    [string]$Jdk = $env:JavaSdkDirectory,
    [string]$Sdk = $env:AndroidSdkDirectory,
    [string]$Avd = "DongoDraw_Pixel",
    [string]$AppiumUrl = "http://127.0.0.1:4723"
)

# Device profile used when an AVD named above has to be created. Anything else is created on the
# phone profile, which is the shape the rest of the suite assumes.
$AvdDeviceProfiles = @{
    "DongoDraw_Pixel" = "pixel_6"
    "DongoDraw_Fold"  = "7.6in Foldable"
}
$SystemImage = "system-images;android-35;google_apis;x86_64"

# NOTE: deliberately NOT 'Stop'. appium/adb/emulator log progress to stderr, which PowerShell
# wraps as NativeCommandError; with 'Stop' that aborts the script on harmless output. Critical
# native steps are checked explicitly via $LASTEXITCODE / Die below instead.
$ErrorActionPreference = "Continue"
$repoRoot = Split-Path -Parent $PSScriptRoot

function Die($msg) { Write-Host $msg -ForegroundColor Red; exit 1 }

if (-not $Jdk) { $Jdk = "C:\Program Files\Microsoft\jdk-17.0.19.10-hotspot" }
if (-not $Sdk) { $Sdk = Join-Path $env:LOCALAPPDATA "Android\Sdk" }

$adb      = Join-Path $Sdk "platform-tools\adb.exe"
$emulator = Join-Path $Sdk "emulator\emulator.exe"

# The Appium server + uiautomator2 driver locate adb/tools via these; JAVA_HOME must be JDK 17.
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

# 1. Appium + driver ------------------------------------------------------------------------------
if (-not (Get-Command appium -ErrorAction SilentlyContinue)) {
    Write-Host "Installing Appium (global)..." -ForegroundColor Cyan
    npm install -g appium
    if ($LASTEXITCODE -ne 0) { Die "npm install -g appium failed." }
}
$drivers = (& appium driver list --installed 2>&1 | Out-String)
if ($drivers -notmatch "uiautomator2") {
    Write-Host "Installing uiautomator2 driver..." -ForegroundColor Cyan
    & appium driver install uiautomator2 2>&1 | Write-Host
}

# 2. Emulator -------------------------------------------------------------------------------------
# Create the AVD on first use rather than making every machine set one up by hand. The foldable is
# the one the rail-layout test wants; the phone profile is the default for everything else.
$existingAvds = (& $emulator -list-avds) | ForEach-Object { $_.Trim() }
if ($existingAvds -notcontains $Avd) {
    $profile = $AvdDeviceProfiles[$Avd]
    if (-not $profile) { $profile = "pixel_6" }
    Write-Host "Creating AVD $Avd ($profile)..." -ForegroundColor Cyan

    $avdManager = Get-ChildItem (Join-Path $Sdk "cmdline-tools") -Filter "avdmanager.bat" -Recurse -ErrorAction SilentlyContinue |
        Select-Object -First 1 -ExpandProperty FullName
    if (-not $avdManager) { Die "avdmanager not found under $Sdk\cmdline-tools. Install the Android SDK command-line tools." }

    # "no" answers avdmanager's "start from a custom hardware profile?" prompt.
    "no" | & $avdManager create avd -n $Avd -k $SystemImage -d $profile | Write-Host
    if ($LASTEXITCODE -ne 0) { Die "Failed to create AVD $Avd. Is $SystemImage installed (sdkmanager)?" }

    # The default 800M data partition fills up once the app plus the seeded images are on it.
    $config = Join-Path $env:USERPROFILE ".android\avd\$Avd.avd\config.ini"
    if (Test-Path $config) {
        (Get-Content $config) -replace '^disk\.dataPartition\.size\s*=.*$', 'disk.dataPartition.size=6442450944' |
            Set-Content $config -Encoding ascii
    }
}

$attached = (& $adb devices) | Select-String "device$"
if (-not $attached) {
    Write-Host "Booting emulator $Avd..." -ForegroundColor Cyan
    Start-Process -FilePath $emulator -ArgumentList "-avd", $Avd, "-no-snapshot-save"
    & $adb wait-for-device
    Wait-For { (& $adb shell getprop sys.boot_completed 2>$null).Trim() -eq "1" } 180 "emulator boot"
}
Write-Host "Device ready." -ForegroundColor Green

# 3. Build + install APK --------------------------------------------------------------------------
Write-Host "Building signed APK..." -ForegroundColor Cyan
# EmbedAssembliesIntoApk=true disables Fast Deployment so the .NET assemblies live INSIDE the APK.
# Without it, a plain `adb install` of a Debug APK launches to a native abort ("No assemblies found
# in .../.__override__") because Fast Deployment expects the assemblies pushed separately (as
# `-t:Run` does). A self-contained APK is required for Appium to install + launch it standalone.
& dotnet build (Join-Path $repoRoot "DongoDraw.csproj") -c Debug --nologo `
    -p:JavaSdkDirectory=$Jdk -p:AndroidSdkDirectory=$Sdk -p:EmbedAssembliesIntoApk=true
if ($LASTEXITCODE -ne 0) { Die "Android build failed." }
$apk = Join-Path $repoRoot "bin\Debug\net9.0-android\com.companyname.DongoDraw-Signed.apk"
if (-not (Test-Path $apk)) { Die "APK not found at $apk" }
& $adb install -r $apk 2>&1 | Write-Host
if ($LASTEXITCODE -ne 0) { Die "adb install failed." }

# The suite asserts first-run behaviour ("No folder selected yet"), so it has to start from a device
# that has never picked a folder. `adb install -r` keeps app data, so anything left behind by a
# previous run - or by someone driving the app by hand - carries over: the settings document still
# holds LastCollection, the app restores that folder on launch, and the empty-state tests fail
# against a populated library. DocumentsUI is cleared for the same reason: the system picker
# reopens wherever it was last used, so a stale location makes "select the default folder" select
# the wrong one.
Write-Host "Clearing app + picker state..." -ForegroundColor Cyan
& $adb shell pm clear com.companyname.DongoDraw 2>&1 | Write-Host
foreach ($picker in @("com.google.android.documentsui", "com.android.documentsui")) {
    & $adb shell pm clear $picker 2>&1 | Out-Null
}

# 4. Appium server + tests ------------------------------------------------------------------------
# A server left listening by an earlier run is the worst failure mode here: this script's own
# Start-Process dies with EADDRINUSE, but Wait-For still sees the port open, so the whole suite
# silently runs against the stale process. Its bundled uiautomator2 server no longer matches the
# installed app and every session dies with "The instrumentation process cannot be initialized" -
# which reads like an app crash rather than a leftover server. Clear the port first.
$port = ([Uri]$AppiumUrl).Port
$stale = Get-NetTCPConnection -LocalPort $port -State Listen -ErrorAction SilentlyContinue
if ($stale) {
    # NOTE: not $pid — that is an automatic read-only variable in PowerShell and assigning to it throws.
    foreach ($owner in ($stale.OwningProcess | Select-Object -Unique)) {
        $proc = Get-Process -Id $owner -ErrorAction SilentlyContinue
        Write-Host "Port $port already held by PID $owner ($($proc.ProcessName), started $($proc.StartTime)); stopping it." -ForegroundColor Yellow
        Stop-Process -Id $owner -Force -ErrorAction SilentlyContinue
    }
    Wait-For { -not (Get-NetTCPConnection -LocalPort $port -State Listen -ErrorAction SilentlyContinue) } 30 "port $port to be released"
}

Write-Host "Starting Appium server..." -ForegroundColor Cyan
# `appium` on PATH is a PowerShell shim (appium.ps1) which Start-Process cannot launch; use the
# .cmd shim next to it. Server logs go to appium-server.log for debugging a failed session.
$appiumCmd = (Get-Command appium.cmd -ErrorAction SilentlyContinue).Source
if (-not $appiumCmd) { $appiumCmd = Join-Path $env:APPDATA "npm\appium.cmd" }
if (-not (Test-Path $appiumCmd)) { Die "appium.cmd not found at $appiumCmd" }
$log = Join-Path $repoRoot "appium-server.log"
$appium = Start-Process -FilePath $appiumCmd -ArgumentList "--relaxed-security" -PassThru `
    -WindowStyle Hidden -RedirectStandardOutput $log -RedirectStandardError "$log.err"
$testExit = 1
try {
    # Readiness is "the server answers /status", not "something holds the port". A TCP check passes
    # against the dying socket of the server we just replaced, and against our own process during the
    # several seconds it spends loading the uiautomator2 driver — both let the suite start too early,
    # and every test then fails with a bare "error occurred while sending the request".
    Wait-For {
        if ($appium.HasExited) { Die "Appium server exited during startup; see $log.err" }
        try { (Invoke-WebRequest -Uri "$AppiumUrl/status" -UseBasicParsing -TimeoutSec 3).StatusCode -eq 200 }
        catch { $false }
    } 90 "Appium server to answer /status"
    Write-Host "Appium server ready." -ForegroundColor Green

    $env:RUN_APPIUM = "1"
    $env:APPIUM_URL = $AppiumUrl
    dotnet test (Join-Path $repoRoot "DongoDraw.UITests\DongoDraw.UITests.csproj") --nologo
    $testExit = $LASTEXITCODE
}
finally {
    # NOTE: no `exit` inside the try. In PowerShell `exit` tears the runspace down without running
    # finally, which is how earlier versions of this script leaked an Appium server on every run —
    # the next run then found port 4723 held and silently drove the stale process.
    # taskkill /T, not Stop-Process: `appium` on PATH is a .cmd shim, so the process started above is
    # cmd.exe and the actual server is its node CHILD. Stopping just the shim leaves node holding
    # port 4723, which is how a server survived to be found by the next run.
    Write-Host "Stopping Appium server..." -ForegroundColor Cyan
    if ($appium -and -not $appium.HasExited) {
        & taskkill /T /F /PID $appium.Id 2>&1 | Out-Null
    }
    Remove-Item Env:\RUN_APPIUM, Env:\APPIUM_URL -ErrorAction SilentlyContinue
}

exit $testExit
