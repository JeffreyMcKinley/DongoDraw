using FigureDrawing.Core;

namespace FigureDrawing.Tests;

// The session aggregate's sequence, counts, skip semantics and time accounting (INV-SES-1..9,
// INV-SUM-*). Its other rule families have their own files: the draft in DrawingSessionSetupTests,
// the pose clock in DrawingSessionCountdownTests, image resolution in DrawingSessionImageTests, and
// the break in DrawingSessionBreakTests. A full start-to-summary lifecycle is in SessionE2ETests.
public class DrawingSessionTests
{
    // A hand-cranked monotonic clock so time accounting is deterministic (no real waiting).
    sealed class FakeClock
    {
        public TimeSpan Now;
        public Func<TimeSpan> Read => () => Now;
        public void Advance(double seconds) => Now += TimeSpan.FromSeconds(seconds);
    }

    static readonly string[] Pool = { "a", "b", "c", "d", "e" };

    // TImage is string with an identity loader, so "the image on screen" and "the image id" are the
    // same value and the sequence stays readable in assertions (INV-IMG-1 — ids are opaque).
    static DrawingSession<string> Make(
        int count,
        IReadOnlyList<string>? pool = null,
        bool shuffle = true,
        int seed = 1234,
        FakeClock? clock = null,
        int seconds = 30) =>
        new(pool ?? Pool, new SessionConfig(seconds, count), id => id, shuffle, new Random(seed),
            (clock ?? new FakeClock()).Read);

    [Fact]
    public void StartsOnAnImageFromThePool()
    {
        var s = Make(count: 3);
        Assert.NotNull(s.CurrentImage);
        Assert.Contains(s.CurrentImage, Pool);
        Assert.False(s.IsComplete);
    }

    [Fact]
    public void ShuffleFalse_ProducesPoolOrder()
    {
        var s = Make(count: 5, shuffle: false);

        var seen = Drain(s);
        Assert.Equal(Pool, seen);
    }

    [Fact]
    public void ShuffleTrue_IsAPermutationOfThePool_NoDropsOrDupes()
    {
        var s = Make(count: 5, shuffle: true, seed: 7);

        var seen = Drain(s);
        Assert.Equal(Pool.OrderBy(x => x), seen.OrderBy(x => x));
    }

    [Fact]
    public void ShuffleTrue_ReordersFromPoolOrder()
    {
        // With this seed the shuffled first pass differs from pool order (guards the shuffle runs).
        var s = Make(count: 5, shuffle: true, seed: 7);
        var seen = Drain(s);
        Assert.NotEqual(Pool, seen);
    }

    [Fact]
    public void SameSeed_ProducesSameSequence()
    {
        var a = Drain(Make(count: 5, seed: 99));
        var b = Drain(Make(count: 5, seed: 99));
        Assert.Equal(a, b);
    }

    [Fact]
    public void NextDecrementsRemainingAndCountsTowardTotal()
    {
        var s = Make(count: 3);
        Assert.Equal(3, s.Remaining);
        Assert.Equal(0, s.CompletedCount);

        s.Next();
        Assert.Equal(1, s.CompletedCount);
        Assert.Equal(2, s.Remaining);
    }

    [Fact]
    public void CompletesWhenConfiguredCountReached()
    {
        var s = Make(count: 3);
        s.Next();
        s.Next();
        Assert.False(s.IsComplete);

        s.Next();
        Assert.True(s.IsComplete);
        Assert.Null(s.CurrentImage);
        Assert.Equal(0, s.Remaining);
        Assert.Equal(3, s.CompletedCount);
    }

    [Fact]
    public void OperationsAfterCompletionAreNoOps()
    {
        var s = Make(count: 1);
        s.Next();
        Assert.True(s.IsComplete);

        s.Next();
        s.Skip();
        s.End();
        Assert.Equal(1, s.CompletedCount);
        Assert.True(s.IsComplete);
    }

    [Fact]
    public void SkipDoesNotCountTowardTotalButChangesImage()
    {
        var s = Make(count: 3, shuffle: false); // deterministic order a,b,c,d,e
        var first = s.CurrentImage;

        s.Skip();
        Assert.Equal(0, s.CompletedCount);
        Assert.Equal(3, s.Remaining);
        Assert.NotEqual(first, s.CurrentImage);
    }

    [Fact]
    public void SkipDoesNotEndTheSession()
    {
        var s = Make(count: 1);
        s.Skip();
        s.Skip();
        Assert.False(s.IsComplete);
        Assert.Equal(0, s.CompletedCount);
    }

    [Fact]
    public void BanksDrawingTimePerCompletedImage()
    {
        var clock = new FakeClock();
        var s = Make(count: 2, clock: clock);

        clock.Advance(30);       // drew first image 30s
        s.Next();
        clock.Advance(25);       // drew second image 25s
        s.Next();

        Assert.True(s.IsComplete);
        Assert.Equal(TimeSpan.FromSeconds(55), s.TotalDrawingTime);
        Assert.Equal(2, s.ImagesDisplayed);
    }

    [Fact]
    public void SkippedTimeIsExcludedFromTotal()
    {
        var clock = new FakeClock();
        var s = Make(count: 1, clock: clock);

        clock.Advance(40);       // spent 40s then skipped -> excluded
        s.Skip();
        clock.Advance(30);       // drew the next image 30s -> counted
        s.Next();

        Assert.Equal(TimeSpan.FromSeconds(30), s.TotalDrawingTime);
        Assert.Equal(1, s.ImagesDisplayed);
    }

    [Fact]
    public void EndBanksCurrentPartialTimeButDoesNotCountTheImage()
    {
        var clock = new FakeClock();
        var s = Make(count: 5, clock: clock);

        clock.Advance(30);
        s.Next();                // image 1 completed, 30s
        clock.Advance(12);       // partway through image 2
        s.End();

        Assert.True(s.IsComplete);
        Assert.Equal(1, s.ImagesDisplayed);                    // partial image not counted
        Assert.Equal(TimeSpan.FromSeconds(42), s.TotalDrawingTime);
    }

    [Fact]
    public void CountLargerThanPool_RepeatsToHonorCount()
    {
        var pool = new[] { "x", "y" };
        var s = Make(count: 5, pool: pool, shuffle: false);

        var seen = Drain(s);
        Assert.Equal(5, seen.Count);                 // honored the configured count
        Assert.All(seen, img => Assert.Contains(img, pool));
    }

    [Fact]
    public void CountLargerThanPool_ShowsEveryImageBeforeRepeating()
    {
        var pool = new[] { "x", "y", "z" };
        var s = Make(count: 3, pool: pool, shuffle: true, seed: 3);

        var firstPass = Drain(s);
        Assert.Equal(pool.OrderBy(v => v), firstPass.OrderBy(v => v)); // full pass, no repeat yet
    }

    [Fact]
    public void EmptyPool_CompletesImmediately()
    {
        var s = Make(count: 3, pool: Array.Empty<string>());
        Assert.True(s.IsComplete);
        Assert.Null(s.CurrentImage);
        Assert.Equal(0, s.ImagesDisplayed);
    }

    [Fact]
    public void ZeroCount_CompletesImmediately()
    {
        var s = Make(count: 0);
        Assert.True(s.IsComplete);
        Assert.Null(s.CurrentImage);
    }

    // Drawing time is time spent drawing. A break between poses, a backgrounded app and an explicit
    // pause all stop the clock; none of them may end up in the total.
    [Fact]
    public void PausedTime_IsNotBankedAsDrawingTime()
    {
        var clock = new FakeClock();
        var s = Make(count: 2, clock: clock);

        clock.Advance(10);
        s.Pause();
        clock.Advance(600);        // ten minutes away from the easel
        s.Resume();
        clock.Advance(20);
        s.Next();

        Assert.Equal(TimeSpan.FromSeconds(30), s.TotalDrawingTime);
    }

    [Fact]
    public void PauseAndResume_AreIdempotent()
    {
        var clock = new FakeClock();
        var s = Make(count: 2, clock: clock);

        clock.Advance(5);
        s.Pause();
        s.Pause();                 // second pause must not re-bank the same segment
        clock.Advance(100);
        s.Resume();
        s.Resume();                // second resume must not restart the segment
        clock.Advance(5);
        s.Next();

        Assert.Equal(TimeSpan.FromSeconds(10), s.TotalDrawingTime);
    }

    [Fact]
    public void EndingWhilePaused_BanksOnlyTheTimeActuallyDrawn()
    {
        var clock = new FakeClock();
        var s = Make(count: 3, clock: clock);

        clock.Advance(12);
        s.Pause();
        clock.Advance(300);
        s.End();

        Assert.Equal(TimeSpan.FromSeconds(12), s.TotalDrawingTime);
    }

    // A fresh image always starts a fresh, running clock — a pause never carries across an advance.
    [Fact]
    public void AdvancingWhilePaused_StartsTheNextImageRunning()
    {
        var clock = new FakeClock();
        var s = Make(count: 3, clock: clock);

        clock.Advance(10);
        s.Pause();
        s.Next();                  // banks 10s, moves to the next image
        Assert.True(s.IsRunning);

        clock.Advance(20);
        s.Next();

        Assert.Equal(TimeSpan.FromSeconds(30), s.TotalDrawingTime);
    }

    [Fact]
    public void PauseAndResume_AreNoOpsOnceComplete()
    {
        var s = Make(count: 1);

        s.Next();
        Assert.True(s.IsComplete);
        Assert.False(s.IsRunning);

        s.Resume();
        Assert.False(s.IsRunning);
    }

    [Fact]
    public void SkippedCount_StartsAtZero_AndOnlySkipRaisesIt()
    {
        var s = Make(count: 4);
        Assert.Equal(0, s.SkippedCount);

        s.Next();
        Assert.Equal(0, s.SkippedCount);

        s.Skip();
        s.Skip();
        Assert.Equal(2, s.SkippedCount);
        Assert.Equal(1, s.CompletedCount);
    }

    [Fact]
    public void Summary_ReportsSkips_AlongsideCompletions()
    {
        var clock = new FakeClock();
        var s = Make(count: 3, clock: clock);

        clock.Advance(10);
        s.Next();                  // completed, 10s banked
        clock.Advance(5);
        s.Skip();                  // not counted, 5s discarded
        clock.Advance(20);
        s.Next();                  // completed, 20s banked

        Assert.Equal(2, s.ImagesDisplayed);
        Assert.Equal(1, s.SkippedCount);
        Assert.Equal(TimeSpan.FromSeconds(30), s.TotalDrawingTime);
    }

    [Fact]
    public void Summary_AveragePoseTime_IsOverCompletedPosesOnly()
    {
        var clock = new FakeClock();
        var s = Make(count: 3, clock: clock);

        clock.Advance(10);
        s.Next();
        clock.Advance(60);
        s.Skip();                  // a long look at an image that never counted
        clock.Advance(30);
        s.Next();

        // 40s over two completed poses — the skipped minute is not in the average.
        Assert.Equal(TimeSpan.FromSeconds(20), s.AveragePoseTime);
    }

    [Fact]
    public void Summary_AveragePoseTime_IsZeroWhenNothingCompleted()
    {
        var s = Make(count: 3);

        s.Skip();
        s.End();

        Assert.Equal(TimeSpan.Zero, s.AveragePoseTime);
    }

    [Fact]
    public void SkipsAfterCompletion_DoNotRaiseTheSkippedCount()
    {
        var s = Make(count: 1);

        s.Next();                  // session complete
        s.Skip();

        Assert.Equal(0, s.SkippedCount);
    }

    // Runs the session to completion via Next(), collecting each displayed image.
    static List<string> Drain(DrawingSession<string> s)
    {
        var seen = new List<string>();
        while (!s.IsComplete)
        {
            seen.Add(s.CurrentImage!);
            s.Next();
        }
        return seen;
    }
}
