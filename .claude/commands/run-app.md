---
description: Boot the DongoDraw emulator (if needed) and deploy + launch the app on it
allowed-tools: Bash(*), PowerShell(*), Read(*)
---

Build the DongoDraw Android app and run it on the `DongoDraw_Pixel` emulator.

Steps:

1. Check for a running device:
   `& "$env:LOCALAPPDATA\Android\Sdk\platform-tools\adb.exe" devices`
   - If a line shows `emulator-5554   device`, the emulator is already up — skip to step 3.
   - If it shows `offline`, wait and re-check until it reads `device`.

2. If no emulator is running, boot it in the background and wait for it to finish booting:
   `& "$env:LOCALAPPDATA\Android\Sdk\emulator\emulator.exe" -avd DongoDraw_Pixel`
   Poll `adb shell getprop sys.boot_completed` until it returns `1`.

3. Deploy and launch. `Directory.Build.props` supplies the Android SDK + JDK 17 paths, so no
   `-p:` overrides are needed:
   `dotnet build DongoDraw.csproj -t:Run -c Debug`
   - If more than one device is attached, force the emulator:
     add `-p:AdbTarget="-s emulator-5554"`.

Notes:
- Use `-t:Run` to deploy — a plain `adb install` of the Debug APK crashes at launch
  ("No assemblies found").
- Report the final build/deploy result. If it fails, quote the shortest decisive error line
  and stop rather than retrying blindly.

$ARGUMENTS
