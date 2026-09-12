using DongoDraw.Data;

namespace DongoDraw.Tests;

// Preferences (Data/Settings over LiteDB): the single document and its own persistence. Runs
// against a real on-disk LiteDB file in a temp directory, deleted on dispose — LiteDB is embedded
// and runs fine on net9.0 desktop.
public sealed class SettingsTests : IDisposable
{
    readonly string databasePath =
        Path.Combine(Path.GetTempPath(), $"dongodraw-test-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        foreach (var file in Sidecars(databasePath))
        {
            try { File.Delete(file); }
            catch (IOException) { /* still held by a deliberately un-disposed instance */ }
        }
    }

    // Everything the store has left on disk at this moment, copied to a fresh path: the datafile and
    // the write-ahead log beside it. This is what a process that died without closing the database
    // leaves behind — the artist swiping the app off the recents list, or the system reclaiming a
    // backgrounded process. Neither runs OnDestroy, so nothing gets the chance to close cleanly.
    string SnapshotAsIfKilled()
    {
        var snapshot = Path.Combine(
            Path.GetTempPath(), $"dongodraw-killed-{Guid.NewGuid():N}.db");

        foreach (var (from, to) in Sidecars(databasePath).Zip(Sidecars(snapshot)))
        {
            if (!File.Exists(from))
                continue;

            // Opened share-all: the original store is still holding these files open, exactly as a
            // live process would be at the instant it is killed.
            using var source = new FileStream(from, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var target = new FileStream(to, FileMode.Create, FileAccess.Write, FileShare.None);
            source.CopyTo(target);
        }

        return snapshot;
    }

    static IEnumerable<string> Sidecars(string path)
    {
        yield return path;
        yield return Path.Combine(
            Path.GetDirectoryName(path)!,
            Path.GetFileNameWithoutExtension(path) + "-log" + Path.GetExtension(path));
    }

    [Fact]
    public void Open_FirstRun_ReturnsDefaults()
    {
        using var settings = Settings.Open(databasePath);

        Assert.Equal(30, settings.PoseDurationSeconds);
        Assert.Equal(20, settings.SessionImageCount);
        Assert.Equal(0, settings.BreakSeconds);
        Assert.True(settings.ShuffleImages);
        Assert.False(settings.GrayscaleMode);
        Assert.True(settings.KeepScreenAwake);
        Assert.False(settings.ChimeOnChange);
        Assert.Null(settings.LastCollection);
    }

    [Fact]
    public void Save_PersistsAcrossInstances()
    {
        using (var settings = Settings.Open(databasePath))
        {
            settings.PoseDurationSeconds = 60;
            settings.SessionImageCount = 40;
            settings.BreakSeconds = 15;
            settings.ShuffleImages = false;
            settings.GrayscaleMode = true;
            settings.KeepScreenAwake = false;
            settings.ChimeOnChange = true;
            // The picked folder tree URI is persisted here, by reference (INV-SET-P5).
            settings.LastCollection = "content://com.android.externalstorage.documents/tree/primary%3APics";
            settings.Save();
        }

        // Reopen from disk — simulates a fresh app launch.
        using var restored = Settings.Open(databasePath);

        Assert.Equal(60, restored.PoseDurationSeconds);
        Assert.Equal(40, restored.SessionImageCount);
        Assert.Equal(15, restored.BreakSeconds);
        Assert.False(restored.ShuffleImages);
        Assert.True(restored.GrayscaleMode);
        Assert.False(restored.KeepScreenAwake);
        Assert.True(restored.ChimeOnChange);
        Assert.Equal(
            "content://com.android.externalstorage.documents/tree/primary%3APics",
            restored.LastCollection);
    }

    // Swiping the app off the recents list kills the process outright: no OnPause, no OnDestroy, and
    // so no clean close of the database. A save that only reaches the write-ahead log and is never
    // folded into the datafile would be lost exactly there — which is the artist picking a folder,
    // closing the app the way people close apps, and finding it forgotten on the next launch.
    [Fact]
    public void Save_SurvivesTheProcessBeingKilledWithoutClosing()
    {
        const string picked = "content://com.android.externalstorage.documents/tree/primary%3APics";

        // Deliberately not disposed: disposing is the thing a killed process never gets to do.
        var settings = Settings.Open(databasePath);
        settings.LastCollection = picked;
        settings.PoseDurationSeconds = 45;
        settings.Save();

        var afterTheKill = SnapshotAsIfKilled();

        try
        {
            using var relaunched = Settings.Open(afterTheKill);

            Assert.Equal(picked, relaunched.LastCollection);
            Assert.Equal(45, relaunched.PoseDurationSeconds);
        }
        finally
        {
            settings.Dispose();
            foreach (var file in Sidecars(afterTheKill).Where(File.Exists))
                File.Delete(file);
        }
    }

    // Probe: killed mid-write, so the log is TRUNCATED rather than whole.
    [Fact]
    public void Save_SurvivesATruncatedLog()
    {
        const string picked = "content://com.android.externalstorage.documents/tree/primary%3APics";

        var settings = Settings.Open(databasePath);
        settings.LastCollection = picked;
        settings.PoseDurationSeconds = 45;
        settings.Save();

        var snapshot = SnapshotAsIfKilled();
        var log = Sidecars(snapshot).Last();

        var before = File.Exists(log) ? new FileInfo(log).Length : 0;
        if (before > 3072)
        {
            using var stream = new FileStream(log, FileMode.Open, FileAccess.Write);
            stream.SetLength(before - 3072);
        }

        try
        {
            using var relaunched = Settings.Open(snapshot);
            Assert.Equal(picked, relaunched.LastCollection);
            Assert.Equal(45, relaunched.PoseDurationSeconds);
        }
        finally
        {
            settings.Dispose();
            foreach (var file in Sidecars(snapshot).Where(File.Exists))
                File.Delete(file);
        }
    }

    // The negative control for the two tests above: with the log thrown away entirely, the value is
    // still there. That is the whole claim — Save checkpoints, so what a killed process leaves in
    // the log is a copy of something already folded into the datafile, not the only copy of it. The
    // day this fails, the durability guarantee has quietly moved back into the log.
    [Fact]
    public void Save_LandsInTheDatafile_NotOnlyInTheLog()
    {
        const string picked = "content://com.android.externalstorage.documents/tree/primary%3APics";

        var settings = Settings.Open(databasePath);
        settings.LastCollection = picked;
        settings.Save();

        var snapshot = SnapshotAsIfKilled();
        var log = Sidecars(snapshot).Last();

        if (File.Exists(log))
            File.Delete(log);

        try
        {
            using var relaunched = Settings.Open(snapshot);
            Assert.Equal(picked, relaunched.LastCollection);
        }
        finally
        {
            settings.Dispose();
            foreach (var file in Sidecars(snapshot).Where(File.Exists))
                File.Delete(file);
        }
    }

    // A log left by a kill can also be garbage rather than short — or, after a device restore, a log
    // belonging to a different database entirely. Either way the outcome has to be defined: the
    // datafile's own values, or defaults. Never a throw at the screen (INV-SET-P6).
    [Fact]
    public void Open_WithAnUnreadableLogBesideIt_StartsFromSomethingUsable()
    {
        const string picked = "content://com.android.externalstorage.documents/tree/primary%3APics";

        using (var settings = Settings.Open(databasePath))
        {
            settings.LastCollection = picked;
            settings.Save();
        }

        File.WriteAllBytes(Sidecars(databasePath).Last(), new byte[8192]);

        using var relaunched = Settings.Open(databasePath);

        Assert.True(
            relaunched.LastCollection == picked || relaunched.LastCollection is null,
            $"Expected the stored folder or a clean default, got '{relaunched.LastCollection}'.");

        // Whatever it decided, the store is usable afterwards.
        relaunched.PoseDurationSeconds = 90;
        relaunched.Save();
    }

    // Saving twice with nothing changed in between must not be a second write: the screen calls Save
    // on every pause, and an unconditional upsert-plus-checkpoint there would be main-thread I/O for
    // nothing (INV-SET-P4).
    [Fact]
    public void Save_WithNothingChanged_WritesNothing()
    {
        using var settings = Settings.Open(databasePath);
        settings.LastCollection = "content://com.android.externalstorage.documents/tree/primary%3APics";
        settings.Save();

        var written = File.GetLastWriteTimeUtc(databasePath);

        settings.Save();
        settings.Save();

        Assert.Equal(written, File.GetLastWriteTimeUtc(databasePath));
    }

    [Fact]
    public void Save_IsUpsert_KeepsSingleDocument()
    {
        using (var settings = Settings.Open(databasePath))
        {
            settings.LastCollection = "content://tree/one";
            settings.Save();

            settings.LastCollection = "content://tree/two";
            settings.Save();
        }

        // Latest write wins; there is exactly one settings document (INV-SET-P1, INV-STO-2).
        using var reopened = Settings.Open(databasePath);

        Assert.Equal("content://tree/two", reopened.LastCollection);
        Assert.Equal(1, reopened.Id);
    }

    // A silently dropped save would cost the user their preferences, so saving through a disposed
    // instance is an error rather than a no-op.
    [Fact]
    public void Save_AfterDispose_Throws()
    {
        var settings = Settings.Open(databasePath);
        settings.Dispose();

        Assert.Throws<ObjectDisposedException>(settings.Save);
    }

    [Fact]
    public void Dispose_IsIdempotent()
    {
        var settings = Settings.Open(databasePath);

        settings.Dispose();
        settings.Dispose();
    }

    // The other half of INV-SET-P6: a file left corrupt by a kill mid-write must cost preferences
    // and nothing else. Throwing here would crash the launch that reads it — every launch, since
    // the bad file stays on disk.
    [Fact]
    public void CorruptDatabase_StartsFromDefaults()
    {
        File.WriteAllBytes(databasePath, "this is not a LiteDB file"u8.ToArray());

        using var settings = Settings.Open(databasePath);

        Assert.Equal(30, settings.PoseDurationSeconds);
        Assert.Null(settings.LastCollection);

        // And it is usable afterwards, not just openable.
        settings.PoseDurationSeconds = 90;
        settings.Save();
    }

    // INV-STO-1: no other type opens a LiteDatabase. Source-level, because a second store would
    // compile and pass every behavioural test while quietly holding a second handle on the file.
    [Fact]
    public void OnlySettings_OpensTheDatabase()
    {
        // Matched on the path *below* the repo root, not the absolute one: a git worktree lives
        // under .claude/worktrees/<branch>, so an absolute match on ".claude" excludes every file in
        // the checkout — including Settings.cs — and the assertion passes by finding nothing at all.
        var owners = Directory
            .EnumerateFiles(TestPaths.RepoRoot, "*.cs", SearchOption.AllDirectories)
            .Select(path => (Path: path, Relative: Path.GetRelativePath(TestPaths.RepoRoot, path)))
            .Where(file => !file.Relative.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") &&
                           !file.Relative.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}") &&
                           !file.Relative.StartsWith($".claude{Path.DirectorySeparatorChar}") &&
                           // This file names the type in the assertion below, not in a call.
                           !file.Relative.EndsWith("SettingsTests.cs"))
            .Select(file => file.Path)
            .Where(path => File.ReadAllText(path).Contains("new LiteDatabase("))
            .Select(Path.GetFileName)
            .ToList();

        Assert.Equal(["Settings.cs"], owners);
    }

    // Losing the database costs preferences and nothing else (INV-SET-P6): a fresh open after the
    // file is gone starts from defaults instead of failing.
    [Fact]
    public void DeletedDatabase_ReopensWithDefaults()
    {
        using (var settings = Settings.Open(databasePath))
        {
            settings.PoseDurationSeconds = 120;
            settings.Save();
        }

        File.Delete(databasePath);

        using var reopened = Settings.Open(databasePath);
        Assert.Equal(30, reopened.PoseDurationSeconds);
    }

    // #8 prevention: documents the concurrency contract. Settings is single-threaded by design
    // (INV-SET-P4 names the write moments, and they are all on the main thread). This test does not
    // make Settings thread-safe — it records what happens when two threads write at once. If LiteDB
    // throws or corrupts, the test documents that fact for anyone touching the threading architecture
    // (e.g. the async session build in SessionActivity). If it survives, the test still pins the
    // behaviour so a future LiteDB upgrade that changes it is visible.
    [Fact]
    public void ConcurrentSaves_DoNotCorruptTheDatabase()
    {
        var settings = Settings.Open(databasePath);

        // Run two threads that write different properties and save. Whether this throws, corrupts,
        // or silently succeeds is the answer this test records. The assertion is that it does not
        // corrupt the file — a reopen afterwards must read valid data.
        var barrier = new ManualResetEventSlim(false);

        var t1 = Task.Run(() =>
        {
            barrier.Wait();
            for (var i = 0; i < 50; i++)
            {
                settings.PoseDurationSeconds = 10 + i;
                settings.Save();
            }
        });

        var t2 = Task.Run(() =>
        {
            barrier.Wait();
            for (var i = 0; i < 50; i++)
            {
                settings.SessionImageCount = 5 + i;
                settings.Save();
            }
        });

        barrier.Set();

        // If LiteDB throws under contention, the exception is caught below and the file integrity
        // assertion still runs regardless.
        try
        {
#pragma warning disable xUnit1031 // Synchronous blocking is intentional: the test exercises concurrent writes
            Task.WaitAll(t1, t2);
#pragma warning restore xUnit1031
        }
        catch (AggregateException)
        {
            // One or both threads faulted — expected under contention. The assertion below checks
            // whether the file survived, which is the point of this test.
        }
        finally
        {
            settings.Dispose();
        }

        // At least one thread must have completed without faulting — if both faulted, the defaults
        // would satisfy the range check and the test would prove nothing about corruption.
        Assert.True(!t1.IsFaulted || !t2.IsFaulted,
            "Both threads faulted — no concurrent writes to test.");

        // The file must still be openable and self-consistent after concurrent writes.
        using var reopened = Settings.Open(databasePath);
        Assert.InRange(reopened.PoseDurationSeconds, 10, 59);
        Assert.InRange(reopened.SessionImageCount, 5, 54);

        // At least one value must differ from the default — otherwise we cannot distinguish "file
        // survived" from "file was silently discarded and replaced with defaults" (INV-SET-P6).
        Assert.True(reopened.PoseDurationSeconds != 30 || reopened.SessionImageCount != 20,
            "Both values are defaults — cannot distinguish survival from silent discard.");
    }
}
