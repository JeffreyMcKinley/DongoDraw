#!/usr/bin/env bash
# Deploys and launches the DongoDraw app on the DongoDraw_Pixel emulator (Git Bash).
#   1. boots the emulator if no device is attached, waits for boot
#   2. deploys + launches via `dotnet build -t:Run` (never a plain adb install — that crashes
#      at launch with "No assemblies found")
# Directory.Build.props supplies the SDK/JDK paths; we also export them for adb/emulator tooling.
#
# Usage: scripts/run-app.sh [emulator-5554]   # optional device id when several are attached
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
avd="DongoDraw_Pixel"
target="${1:-}"

jdk="${JavaSdkDirectory:-/c/Program Files/Microsoft/jdk-17.0.19.10-hotspot}"
sdk="${AndroidSdkDirectory:-$LOCALAPPDATA/Android/Sdk}"
export ANDROID_HOME="$sdk" ANDROID_SDK_ROOT="$sdk" JAVA_HOME="$jdk"

adb="$sdk/platform-tools/adb.exe"
emulator="$sdk/emulator/emulator.exe"

# 1. Emulator
if ! "$adb" devices | grep -qE 'device$'; then
    echo "Booting emulator $avd..."
    "$emulator" -avd "$avd" &
    "$adb" wait-for-device
    until [ "$("$adb" shell getprop sys.boot_completed 2>/dev/null | tr -d '\r')" = "1" ]; do
        sleep 2
    done
fi
echo "Device ready."

# 2. Deploy + launch
echo "Deploying to device..."
args=("$repo_root/DongoDraw.csproj" -t:Run -c Debug --nologo
      "-p:JavaSdkDirectory=$jdk" "-p:AndroidSdkDirectory=$sdk")
[ -n "$target" ] && args+=("-p:AdbTarget=-s $target")

dotnet build "${args[@]}"
echo "App launched on device."
