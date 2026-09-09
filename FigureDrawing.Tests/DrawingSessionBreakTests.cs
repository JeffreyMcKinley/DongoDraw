using FigureDrawing.Core;

namespace FigureDrawing.Tests;

// The pose/break state machine (INV-SES-10..12, INV-POSE-*): advancing a pose restarts its clock,
// a break never counts a pose or follows the last one, and a skip lands on the next pose rather
// than on a rest. The pairing "advance the pose AND restart the clock" is a domain rule, asserted
// here rather than left to a screen (docs/ARCHITECTURE.md §17).
public class DrawingSessionBreakTests
{
    // A hand-cranked monotonic clock so every timing assertion is deterministic (no real waiting).
    sealed class FakeClock
    {
        public TimeSpan Now;
        public Func<TimeSpan> Read => () => Now;
        public void Advance(double seconds) => Now += TimeSpan.FromSeconds(seconds);
    }

    static readonly string[] Pool = { "a", "b", "c", "d" };

    // Loader that resolves every id to itself, so "the image on screen" is directly assertable.
    static string? Echo(string id) => id;

    static DrawingSession<string> Make(
        FakeClock clock,
        int seconds = 30,
        int count = 3,
        int breakSeconds = 0,
        IReadOnlyList<string>? pool = null,
        Func<string, string?>? load = null,
        Action<string>? onUnreadable = null)
        =>
        new(pool ?? Pool, new SessionConfig(seconds, count, breakSeconds), load ?? Echo,
            shuffle: false, random: new Random(1), clock: clock.Read, onUnreadable: onUnreadable);

    [Fact]
    public void StartsOnTheFirstPose_WithAFullClock()
    {
        var clock = new FakeClock();
        var s = Make(clock, seconds: 30);

        Assert.Equal(SessionPhase.Pose, s.Phase);
        Assert.Equal("a", s.CurrentImage);
        Assert.False(s.IsComplete);
        Assert.False(s.OnBreak);
        Assert.Equal("0:30", s.Display);
        Assert.Equal(100, s.RemainingPercent);
        Assert.Equal(1, s.CurrentPoseNumber);
        Assert.Equal(3, s.TargetCount);
    }

    [Fact]
    public void EmptyPool_IsCompleteBeforeTheFirstTick()
    {
        var clock = new FakeClock();
        var s = Make(clock, pool: Array.Empty<string>());

        Assert.Equal(SessionPhase.Complete, s.Phase);
        Assert.True(s.IsComplete);
        Assert.Null(s.CurrentImage);
    }

    [Fact]
    public void ZeroCount_IsCompleteBeforeTheFirstTick()
    {
        var clock = new FakeClock();
        var s = Make(clock, count: 0);

        Assert.True(s.IsComplete);
    }

    [Fact]
    public void PoseExpiry_CountsThePose_AndRestartsTheClockForTheNextOne()
    {
        var clock = new FakeClock();
        var s = Make(clock, seconds: 30, count: 3);

        clock.Advance(30);
        Assert.Equal(SessionTick.PoseStarted, s.Tick());

        Assert.Equal(1, s.CompletedCount);
        Assert.Equal("b", s.CurrentImage);
        Assert.Equal(SessionPhase.Pose, s.Phase);
        // The clock is back at full for the new pose — the bug this aggregate prevents.
        Assert.Equal("0:30", s.Display);
        Assert.Equal(2, s.CurrentPoseNumber);
    }

    [Fact]
    public void Tick_BeforeExpiry_DoesNothing()
    {
        var clock = new FakeClock();
        var s = Make(clock, seconds: 30);

        clock.Advance(10);

        Assert.Equal(SessionTick.None, s.Tick());
        Assert.Equal("a", s.CurrentImage);
        Assert.Equal(0, s.CompletedCount);
        Assert.Equal("0:20", s.Display);
    }

    [Fact]
    public void Tick_WhilePaused_NeverExpiresThePose()
    {
        var clock = new FakeClock();
        var s = Make(clock, seconds: 30);

        s.Pause();
        clock.Advance(120);

        Assert.Equal(SessionTick.None, s.Tick());
        Assert.Equal(0, s.CompletedCount);
        Assert.Equal("0:30", s.Display);
    }

    [Fact]
    public void PauseThenResume_ResumesWhereThePoseLeftOff()
    {
        var clock = new FakeClock();
        var s = Make(clock, seconds: 30);

        clock.Advance(10);
        s.Pause();
        clock.Advance(300);        // backgrounded for five minutes
        s.Resume();

        Assert.Equal("0:20", s.Display);
        Assert.False(s.IsComplete);
    }

    [Fact]
    public void WithABreak_PoseExpiryEntersTheBreak_OnItsOwnClock()
    {
        var clock = new FakeClock();
        var s = Make(clock, seconds: 30, count: 3, breakSeconds: 15);

        clock.Advance(30);
        s.Tick();

        Assert.Equal(SessionPhase.Break, s.Phase);
        Assert.True(s.OnBreak);
        Assert.Equal("0:15", s.Display);
        // The pose already counted; the next image is loaded underneath the overlay.
        Assert.Equal(1, s.CompletedCount);
        Assert.Equal("b", s.CurrentImage);
    }

    [Fact]
    public void BreakExpiry_ReturnsToThePose_WithAFullPoseClock()
    {
        var clock = new FakeClock();
        var s = Make(clock, seconds: 30, count: 3, breakSeconds: 15);

        clock.Advance(30);
        s.Tick();                  // into the break
        clock.Advance(15);
        Assert.Equal(SessionTick.PoseStarted, s.Tick());     // out of it

        Assert.Equal(SessionPhase.Pose, s.Phase);
        Assert.False(s.OnBreak);
        Assert.Equal("0:30", s.Display);
        // A break is rest, not drawing: it does not count a second pose.
        Assert.Equal(1, s.CompletedCount);
    }

    // Backgrounding during a rest: the break's own clock freezes, and the session clock — already
    // stopped for the break — must not be restarted by the resume (INV-SES-12, INV-CD-2).
    [Fact]
    public void PausingDuringABreak_BanksNoTime_AndResumesTheRest()
    {
        var clock = new FakeClock();
        var s = Make(clock, seconds: 30, count: 3, breakSeconds: 15);

        clock.Advance(30);
        s.Tick();                  // pose 1 complete -> break
        Assert.True(s.OnBreak);

        clock.Advance(5);          // five seconds into the rest
        s.Pause();
        clock.Advance(300);        // five minutes in the background
        s.Resume();

        Assert.True(s.OnBreak);
        Assert.Equal("0:10", s.Display);          // the rest picks up where it left off

        clock.Advance(10);
        Assert.Equal(SessionTick.PoseStarted, s.Tick());                    // break over -> pose 2

        Assert.Equal(SessionPhase.Pose, s.Phase);
        Assert.Equal("0:30", s.Display);
        Assert.Equal(1, s.CompletedCount);
        Assert.Equal(TimeSpan.FromSeconds(30), s.TotalDrawingTime);   // no rest, no background time
    }

    [Fact]
    public void WithoutABreak_PoseRunsStraightIntoTheNext()
    {
        var clock = new FakeClock();
        var s = Make(clock, seconds: 30, count: 3, breakSeconds: 0);

        clock.Advance(30);
        s.Tick();

        Assert.Equal(SessionPhase.Pose, s.Phase);
        Assert.False(s.OnBreak);
    }

    [Fact]
    public void LastPose_CompletesTheSession_WithoutATrailingBreak()
    {
        var clock = new FakeClock();
        var s = Make(clock, seconds: 30, count: 2, breakSeconds: 15);

        clock.Advance(30);
        s.Tick();                  // pose 1 done -> break
        clock.Advance(15);
        s.Tick();                  // -> pose 2
        clock.Advance(30);
        s.Tick();                  // pose 2 done -> session over, no break after it

        Assert.Equal(SessionPhase.Complete, s.Phase);
        Assert.True(s.IsComplete);
        Assert.False(s.OnBreak);
        Assert.Null(s.CurrentImage);
    }

    [Fact]
    public void Next_IsTheManualDoneTap_CountingThePose()
    {
        var clock = new FakeClock();
        var s = Make(clock, seconds: 30, count: 3);

        clock.Advance(10);
        s.Next();

        Assert.Equal(1, s.CompletedCount);
        Assert.Equal("b", s.CurrentImage);
        Assert.Equal("0:30", s.Display);
    }

    [Fact]
    public void Skip_DoesNotCountThePose_AndSkipsThePendingBreak()
    {
        var clock = new FakeClock();
        var s = Make(clock, seconds: 30, count: 3, breakSeconds: 15);

        clock.Advance(10);
        s.Skip();

        Assert.Equal(0, s.CompletedCount);
        Assert.Equal(1, s.SkippedCount);
        Assert.Equal(SessionPhase.Pose, s.Phase);      // straight to the next image, no rest
        Assert.Equal("b", s.CurrentImage);
        Assert.Equal("0:30", s.Display);
    }

    // The image under the break overlay is the *next* pose's, so a done-tap during a rest ends the
    // rest rather than counting a pose nobody has drawn yet (INV-SES-10).
    [Fact]
    public void Next_DuringABreak_EndsTheRestWithoutCountingAPose()
    {
        var clock = new FakeClock();
        var s = Make(clock, seconds: 30, count: 3, breakSeconds: 60);

        clock.Advance(30);
        s.Tick();                  // pose 1 complete -> a one-minute break
        Assert.Equal(1, s.CompletedCount);

        clock.Advance(10);
        s.Next();                  // "done" tapped while resting

        Assert.Equal(SessionPhase.Pose, s.Phase);
        Assert.Equal(1, s.CompletedCount);          // still one pose drawn, not two
        Assert.Equal("b", s.CurrentImage);          // the same image the break was covering
        Assert.Equal("0:30", s.Display);
        Assert.Equal(TimeSpan.FromSeconds(30), s.TotalDrawingTime);
    }

    // The session clock is stopped for a rest, so an IsRunning that tracked it would read false here.
    // It tracks the phase countdown instead — the break has one, and it is draining.
    [Fact]
    public void IsRunning_TracksThePhaseClock_NotTheDrawingClock()
    {
        var clock = new FakeClock();
        var s = Make(clock, seconds: 30, count: 3, breakSeconds: 15);

        Assert.True(s.IsRunning);

        clock.Advance(30);
        s.Tick();                  // into the break

        Assert.True(s.OnBreak);
        Assert.True(s.IsRunning);  // the rest is timed, even though no drawing time is banked

        s.Pause();
        Assert.False(s.IsRunning);
    }

    [Fact]
    public void Skip_DuringABreak_StartsTheNextPoseImmediately()
    {
        var clock = new FakeClock();
        var s = Make(clock, seconds: 30, count: 3, breakSeconds: 60);

        clock.Advance(30);
        s.Tick();                  // into a one-minute break
        s.Skip();

        Assert.Equal(SessionPhase.Pose, s.Phase);
        Assert.Equal("0:30", s.Display);
    }

    [Fact]
    public void End_StopsTheSession_AndBanksThePartialPose()
    {
        var clock = new FakeClock();
        var s = Make(clock, seconds: 30, count: 5);

        clock.Advance(30);
        s.Tick();                  // one full pose banked
        clock.Advance(12);
        s.End();

        Assert.True(s.IsComplete);
        Assert.Null(s.CurrentImage);
        Assert.Equal(1, s.ImagesDisplayed);
        Assert.Equal(TimeSpan.FromSeconds(42), s.TotalDrawingTime);
    }

    [Fact]
    public void CommandsAfterCompletion_AreNoOps()
    {
        var clock = new FakeClock();
        var s = Make(clock, seconds: 30, count: 1);

        clock.Advance(30);
        s.Tick();
        Assert.True(s.IsComplete);

        var (displayed, drawn, skipped) = (s.ImagesDisplayed, s.TotalDrawingTime, s.SkippedCount);
        s.Next();
        s.Skip();
        s.End();
        s.Resume();

        Assert.Equal(SessionTick.None, s.Tick());
        Assert.Equal((displayed, drawn, skipped), (s.ImagesDisplayed, s.TotalDrawingTime, s.SkippedCount));
        Assert.True(s.IsComplete);
    }

    [Theory]
    [InlineData(0, 100)]
    [InlineData(15, 50)]
    [InlineData(30, 0)]
    [InlineData(45, 0)]        // clamped: an overrun never reads negative
    public void RemainingPercent_TracksTheClockDown(double elapsed, int expected)
    {
        var clock = new FakeClock();
        var s = Make(clock, seconds: 30);

        clock.Advance(elapsed);

        Assert.Equal(expected, s.RemainingPercent);
    }

    [Fact]
    public void CurrentPoseNumber_StopsAtTheTarget()
    {
        var clock = new FakeClock();
        var s = Make(clock, seconds: 30, count: 2);

        Assert.Equal(1, s.CurrentPoseNumber);

        clock.Advance(30);
        s.Tick();
        Assert.Equal(2, s.CurrentPoseNumber);

        clock.Advance(30);
        s.Tick();                  // session over — the label must not read "Image 3 of 2"
        Assert.Equal(2, s.CurrentPoseNumber);
    }

    [Fact]
    public void UnreadableImages_AreSkippedPast_AndReported()
    {
        var clock = new FakeClock();
        var reported = new List<string>();
        var s = Make(clock, count: 3,
            load: id => id == "a" ? null : id,
            onUnreadable: reported.Add);

        Assert.Equal("b", s.CurrentImage);
        Assert.Equal(new[] { "a" }, reported);
        Assert.False(s.CouldNotDisplayImage);
    }

    [Fact]
    public void AnEntirelyUndisplayablePool_CompletesInTheErrorState()
    {
        var clock = new FakeClock();
        var s = Make(clock, count: 3, load: _ => null);

        Assert.True(s.IsComplete);
        Assert.True(s.CouldNotDisplayImage);
        Assert.Null(s.CurrentImage);
    }

    // The screen plays the change-of-pose tone on what Tick reports, so "a rest started" and "a new
    // pose started" have to be different answers. Reconstructing them from the state afterwards is
    // what put this rule in an Activity in the first place.
    [Fact]
    public void PoseExpiry_WithABreakConfigured_ReportsTheBreakStarting()
    {
        var clock = new FakeClock();
        var s = Make(clock, seconds: 30, count: 3, breakSeconds: 15);

        clock.Advance(30);

        Assert.Equal(SessionTick.BreakStarted, s.Tick());
    }

    [Fact]
    public void BreakExpiry_ReportsThePoseStarting()
    {
        var clock = new FakeClock();
        var s = Make(clock, seconds: 30, count: 3, breakSeconds: 15);

        clock.Advance(30);
        s.Tick();                  // into the rest
        clock.Advance(15);

        Assert.Equal(SessionTick.PoseStarted, s.Tick());
    }

    // A session ends on a pose, never on a rest (INV-SES-10), so the last tick reports completion
    // whether or not a break is configured — and never a pose, which would chime for an image
    // nobody is going to draw.
    [Theory]
    [InlineData(0)]
    [InlineData(15)]
    public void TheTickThatReachesTheCount_ReportsCompletion(int breakSeconds)
    {
        var clock = new FakeClock();
        var s = Make(clock, seconds: 30, count: 1, breakSeconds: breakSeconds);

        clock.Advance(30);

        Assert.Equal(SessionTick.Completed, s.Tick());
    }
}
