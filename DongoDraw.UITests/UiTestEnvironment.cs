using System.Diagnostics;

namespace DongoDraw.UITests;

// Shared config for the Appium end-to-end tests: repo/APK paths and the run gate.
internal static class UiTestEnvironment
{
    public const string AppPackage = "com.companyname.DongoDraw";
    public const string SignedApkRelative = @"bin\Debug\net9.0-android\com.companyname.DongoDraw-Signed.apk";

    // The folder the tests grant to the app. It is a real directory on the emulator's shared storage
    // whose contents the tests set via adb, so "select the default folder" deterministically
    // exercises the empty vs populated cases.
    //
    // It must be a SUBFOLDER of shared storage, never the root: Android refuses to grant the storage
    // root through ACTION_OPEN_DOCUMENT_TREE ("Can't use this folder — to protect your privacy,
    // choose another folder") and shows no confirm button at all, which is where the picker opens on
    // a DocumentsUI with no history.
    public const string DefaultPickerDir = "/sdcard/Audiobooks";

    // The picker lists folders by name, which is how the tests navigate into the one above.
    public static string DefaultPickerFolderName => DefaultPickerDir.Split('/').Last();

    // Appium is heavy (needs an emulator + a running Appium server) and flaky, so it is opt-in:
    // the whole suite only runs when RUN_APPIUM=1. Otherwise every test skips-as-pass, keeping
    // `nx test` fast and deterministic.
    public static bool Enabled =>
        Environment.GetEnvironmentVariable("RUN_APPIUM") == "1";

    public static Uri ServerUri =>
        new(Environment.GetEnvironmentVariable("APPIUM_URL") ?? "http://127.0.0.1:4723");

    public static string RepoRoot { get; } = FindRepoRoot();

    public static string SignedApkPath => Path.Combine(RepoRoot, SignedApkRelative);

    // Fully-qualified Android resource id, e.g. com.companyname.DongoDraw:id/pick_button.
    public static string ViewId(string localId) => $"{AppPackage}:id/{localId}";

    static string Adb => Path.Combine(
        Environment.GetEnvironmentVariable("ANDROID_HOME")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Android", "Sdk"),
        "platform-tools", "adb.exe");

    // Runs an adb command and returns stdout; throws if adb exits non-zero.
    public static string RunAdb(params string[] args)
    {
        var psi = new ProcessStartInfo(Adb) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEnd();
        var stderr = p.StandardError.ReadToEnd();
        p.WaitForExit();
        if (p.ExitCode != 0)
            throw new InvalidOperationException($"adb {string.Join(' ', args)} failed: {stderr}");
        return stdout;
    }

    // A minimal valid 1x1 PNG (red pixel). Decoded on the host and pushed as a real file so shell
    // escaping can't corrupt it — enough to exercise the enumerate -> decode -> display path.
    const string OnePixelPngBase64 =
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==";

    // Wipes the app's data: the settings document (which remembers the last picked folder and would
    // otherwise make the app restore a populated library on launch) and its persisted URI grants.
    // A test asserting first-run behaviour has to do this itself — the suite shares one app install
    // across every test, so a sibling that picks a folder leaves that choice behind.
    public static void ResetAppState() => RunAdb("shell", "pm", "clear", AppPackage);

    // Replaces the default picker folder's contents: empties it, then pushes `imageCount` valid
    // PNGs. The files go over in ONE `adb push` of a staging directory — a push per file costs a
    // process launch each and this runs before most tests in the suite.
    public static void SeedDefaultFolder(int imageCount)
    {
        RunAdb("shell", "rm", "-rf", DefaultPickerDir);
        RunAdb("shell", "mkdir", "-p", DefaultPickerDir);

        if (imageCount > 0)
        {
            // Unique per call: two device runs sharing this host (Pixel and Fold) would otherwise
            // delete each other's staging directory mid-push.
            var staging = Path.Combine(Path.GetTempPath(), $"fd-seed-{Guid.NewGuid():N}");
            Directory.CreateDirectory(staging);

            var png = Convert.FromBase64String(OnePixelPngBase64);
            for (var i = 0; i < imageCount; i++)
                File.WriteAllBytes(Path.Combine(staging, SeededImageName(i)), png);

            try
            {
                // Trailing "/." pushes the CONTENTS of the staging dir, not the dir itself.
                RunAdb("push", Path.Combine(staging, "."), DefaultPickerDir);
            }
            finally
            {
                Directory.Delete(staging, recursive: true);
            }
        }

        RunAdb("shell", "am", "broadcast", "-a", "android.intent.action.MEDIA_SCANNER_SCAN_FILE",
            "-d", $"file://{DefaultPickerDir}");
    }

    // The distinguishing part of Resources/values/strings.xml's empty_folder_text. Owned here rather
    // than spelled out at each assertion so a copy edit has one place to be reflected — the same
    // reason SeededImageName exists.
    public const string EmptyFolderMessage = "No images found";

    // The distinguishing part of library_loading_text. The one caption that is on screen for exactly
    // as long as a load is running, which makes "it is gone" the only precise way to wait for a load
    // to finish: the pool count is retained across a re-walk of the same folder, so it is already
    // populated before the new walk lands.
    public const string LoadingMessage = "Reading that folder";


    // Adds images to the already-seeded folder without disturbing what is there, standing in for the
    // artist dropping files in from another app. SeedDefaultFolder wipes the folder first, so it
    // cannot be reused for this: the point is that the app notices files it has not seen before.
    public static void AddImagesToDefaultFolder(int firstIndex, int count)
    {
        var staging = Path.Combine(Path.GetTempPath(), "fd-seed-extra");
        if (Directory.Exists(staging))
            Directory.Delete(staging, recursive: true);

        Directory.CreateDirectory(staging);

        var png = Convert.FromBase64String(OnePixelPngBase64);
        for (var i = 0; i < count; i++)
            File.WriteAllBytes(Path.Combine(staging, SeededImageName(firstIndex + i)), png);

        RunAdb("push", Path.Combine(staging, "."), DefaultPickerDir);

        RunAdb("shell", "am", "broadcast", "-a", "android.intent.action.MEDIA_SCANNER_SCAN_FILE",
            "-d", $"file://{DefaultPickerDir}");

        // Verified rather than assumed: a push that silently lands in the wrong place would surface
        // fifteen seconds later as a wrong library count, blaming the app for a seeding failure.
        var listing = RunAdb("shell", "ls", DefaultPickerDir);
        for (var i = 0; i < count; i++)
        {
            var name = SeededImageName(firstIndex + i);
            if (!listing.Contains(name, StringComparison.Ordinal))
                throw new InvalidOperationException(
                    $"Seeding failed: {name} is not in {DefaultPickerDir}. Listing: {listing}");
        }
    }

    // The name of the nth seeded image. Owned here so a test asserting on a seeded file cannot
    // drift from what seeding actually writes.
    public static string SeededImageName(int index) => $"img{index}.png";

    // Wipes the system picker's own state — its last-browsed location above all. DocumentsUI
    // reopens where it was left, which is indistinguishable from the app supplying a starting
    // point, so a test asserting that the app put the picker somewhere has to clear this first.
    // The app's persisted URI grants live in the system, not in DocumentsUI's data, and survive it.
    //
    // Both package names are tried: the picker is `com.google.android.documentsui` on Play-flavoured
    // images and `com.android.documentsui` on AOSP ones, and clearing the absent one fails
    // harmlessly.
    public static void ResetPickerState()
    {
        var cleared = false;

        foreach (var package in new[] { "com.google.android.documentsui", "com.android.documentsui" })
        {
            try
            {
                RunAdb("shell", "pm", "clear", package);
                cleared = true;
            }
            catch (InvalidOperationException)
            {
                // Not the picker on this image; the other name is.
            }
        }

        // Swallowing both failures would leave DocumentsUI reopening where it was last left, which is
        // exactly what the test calling this is trying to rule out — it would then pass without the
        // app ever supplying a starting point.
        if (!cleared)
            throw new InvalidOperationException("Could not clear the system picker's state; no DocumentsUI package responded.");
    }

    // Kills the app's process the way the system does when it reclaims a backgrounded app: no
    // OnPause, no OnDestroy, no chance to close the database. `am force-stop` (what TerminateApp
    // uses) is the harsher cousin that also cancels alarms and stops the package; this one is the
    // shape the reported bug arrives in.
    public static void KillAppProcess() => RunAdb("shell", "am", "kill", AppPackage);

    static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "DongoDraw.sln")))
                return dir.FullName;
            dir = dir.Parent;
        }

        throw new InvalidOperationException(
            $"Could not locate DongoDraw.sln above {AppContext.BaseDirectory}");
    }
}
