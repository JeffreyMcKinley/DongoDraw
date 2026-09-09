using FigureDrawing.Core;

namespace FigureDrawing.Tests;

// The session's pose clock (INV-CD-*): how much time the pose on screen has left, whether it is
// draining, and how it reads. Every test drives a fake monotonic clock, so the suite is
// deterministic and instant — no Thread.Sleep, no real seconds.
public class DrawingSessionCountdownTests
{
    // A hand-cranked monotonic clock: tests move time forward explicitly.
    sealed class FakeClock
    {
        public TimeSpan Now { get; private set; }

        public Func<TimeSpan> Read => () => Now;

        public void Advance(double seconds) => Now += TimeSpan.FromSeconds(seconds);
    }

    static readonly string[] Pool = ["a", "b", "c"];

    // A running session whose pose lasts `seconds`. count is high enough that advancing a pose never
    // ends the run, so the clock rules can be observed across poses.
    static (DrawingSession<string> Session, FakeClock Clock) Start(
        int seconds = 30, int count = 10, int breakSeconds = 0)
    {
        var clock = new FakeClock();
        var session = new DrawingSession<string>(
            Pool, new SessionConfig(seconds, count, breakSeconds), id => id,
            shuffle: false, random: new Random(1), clock: clock.Read);

        return (session, clock);
    }

    [Fact]
    public void NewPose_IsRunning_WithTheFullDurationRemaining()
    {
        var (session, _) = Start(30);

        Assert.True(session.IsRunning);
        Assert.False(session.IsPaused);
        Assert.False(session.IsExpired);
        Assert.Equal(TimeSpan.FromSeconds(30), session.TimeRemaining);
        Assert.Equal(30, session.SecondsRemaining);
        Assert.Equal(TimeSpan.FromSeconds(30), session.PhaseDuration);
    }

    [Fact]
    public void TimeRemaining_DrainsWithTheClock()
    {
        var (session, clock) = Start(30);

        clock.Advance(10);
        Assert.Equal(TimeSpan.FromSeconds(20), session.TimeRemaining);

        clock.Advance(19.5);
        Assert.Equal(TimeSpan.FromSeconds(0.5), session.TimeRemaining);
    }

    // The clock is monotonic, not tick-driven: reading it rarely (a dropped frame, a busy main
    // thread) must not make the pose last longer than configured (INV-CD-1).
    [Fact]
    public void TimeRemaining_DoesNotDrift_WhenPolledIrregularly()
    {
        var (session, clock) = Start(60);

        for (var i = 0; i < 7; i++)
            clock.Advance(0.137);          // ragged, non-second-aligned polling

        clock.Advance(59.041);
        Assert.True(session.IsExpired);
        Assert.Equal(TimeSpan.Zero, session.TimeRemaining);
    }

    // Displayed seconds round UP so a fresh pose reads its full length and "0" appears only at zero.
    [Theory]
    [InlineData(0.0, 30)]
    [InlineData(0.4, 30)]
    [InlineData(1.0, 29)]
    [InlineData(29.2, 1)]
    [InlineData(30.0, 0)]
    [InlineData(45.0, 0)]
    public void SecondsRemaining_RoundsUp(double elapsed, int expected)
    {
        var (session, clock) = Start(30);

        clock.Advance(elapsed);

        Assert.Equal(expected, session.SecondsRemaining);
    }

    [Fact]
    public void TimeRemaining_NeverGoesNegative()
    {
        var (session, clock) = Start(5);

        clock.Advance(500);

        Assert.Equal(TimeSpan.Zero, session.TimeRemaining);
        Assert.Equal(0, session.SecondsRemaining);
        Assert.True(session.IsExpired);
        Assert.False(session.IsRunning);
    }

    [Theory]
    [InlineData(0, "0:00")]
    [InlineData(5, "0:05")]
    [InlineData(59, "0:59")]
    [InlineData(60, "1:00")]
    [InlineData(90, "1:30")]
    [InlineData(600, "10:00")]
    [InlineData(3600, "1:00:00")]
    [InlineData(3725, "1:02:05")]
    [InlineData(-3, "0:00")]
    public void Format_RendersMinutesAndSeconds(int seconds, string expected) =>
        Assert.Equal(expected, DrawingSession.Format(seconds));

    [Fact]
    public void Display_UsesTheRoundedUpRemainingTime()
    {
        var (session, clock) = Start(90);

        Assert.Equal("1:30", session.Display);

        clock.Advance(30.5);
        Assert.Equal("1:00", session.Display);
    }

    [Fact]
    public void RemainingPercent_EmptiesAsThePoseRuns()
    {
        var (session, clock) = Start(60);

        Assert.Equal(100, session.RemainingPercent);

        clock.Advance(30);
        Assert.Equal(50, session.RemainingPercent);

        clock.Advance(60);
        Assert.Equal(0, session.RemainingPercent);
    }

    // OnPause: time must stop moving while the app is backgrounded (INV-CD-2).
    [Fact]
    public void Pause_FreezesTheRemainingTime()
    {
        var (session, clock) = Start(30);

        clock.Advance(10);
        session.Pause();
        clock.Advance(120);          // a long spell in the background

        Assert.True(session.IsPaused);
        Assert.False(session.IsRunning);
        Assert.False(session.IsExpired);
        Assert.Equal(TimeSpan.FromSeconds(20), session.TimeRemaining);
    }

    // OnResume: the pose picks up exactly where it left off, not from the top and not from a
    // background-shifted clock.
    [Fact]
    public void Resume_ContinuesFromWhereItPaused()
    {
        var (session, clock) = Start(30);

        clock.Advance(10);
        session.Pause();
        clock.Advance(120);
        session.Resume();

        Assert.True(session.IsRunning);
        Assert.Equal(TimeSpan.FromSeconds(20), session.TimeRemaining);

        clock.Advance(5);
        Assert.Equal(TimeSpan.FromSeconds(15), session.TimeRemaining);
    }

    // Lifecycle callbacks can arrive doubled up; neither call may lose or gain time (INV-CD-3).
    [Fact]
    public void PauseAndResume_AreIdempotent()
    {
        var (session, clock) = Start(30);

        clock.Advance(4);
        session.Pause();
        session.Pause();
        clock.Advance(50);
        session.Resume();
        session.Resume();
        clock.Advance(6);

        Assert.Equal(TimeSpan.FromSeconds(20), session.TimeRemaining);
    }

    // INV-CD-3: a resume leaves an expired pose expired — but it must still un-pause, or the next
    // Tick (which bails while paused) can never retire it and the screen sits at 0:00 forever.
    [Fact]
    public void Resume_LeavesAnExpiredPoseExpired_ButUnpausedSoTheNextTickRetiresIt()
    {
        var (session, clock) = Start(10);

        clock.Advance(10);
        session.Pause();
        session.Resume();

        Assert.True(session.IsExpired);
        Assert.Equal(TimeSpan.Zero, session.TimeRemaining);
        Assert.False(session.IsPaused);
    }

    // The stranding case: paused after expiry, away for two minutes, back. The pose must retire on
    // the next tick, and the two minutes away must not be banked as drawing time (INV-CD-2).
    [Fact]
    public void PausedAfterExpiry_ResumesAndRetiresThePoseOnTheNextTick()
    {
        var (session, clock) = Start(10);

        clock.Advance(10);
        session.Pause();
        clock.Advance(120);
        session.Resume();

        Assert.False(session.IsPaused);
        Assert.Equal(SessionTick.PoseStarted, session.Tick());
        Assert.Equal(1, session.CompletedCount);
        Assert.True(session.TotalDrawingTime <= TimeSpan.FromSeconds(10));
    }

    [Fact]
    public void ANewSession_IsNotPausedByTheUser()
    {
        var (session, _) = Start();

        Assert.False(session.PausedByUser);
        Assert.False(session.IsPaused);
    }

    [Fact]
    public void LifecyclePause_StopsTheClockWithoutClaimingTheUserAskedForIt()
    {
        var (session, _) = Start();

        session.Pause();

        Assert.True(session.IsPaused);
        Assert.False(session.PausedByUser);
    }

    [Fact]
    public void UserPause_IsRemembered()
    {
        var (session, _) = Start();

        session.Pause(PauseReason.User);

        Assert.True(session.IsPaused);
        Assert.True(session.PausedByUser);
    }

    // The case the screen depends on: paused deliberately, then backgrounded. Coming back must not
    // resume a pose the drawer stopped, so a lifecycle pause never downgrades a user pause.
    [Fact]
    public void ALifecyclePauseDoesNotDowngradeAUserPause()
    {
        var (session, _) = Start();

        session.Pause(PauseReason.User);
        session.Pause();

        Assert.True(session.PausedByUser);
    }

    [Fact]
    public void Resume_ClearsTheUserPause()
    {
        var (session, _) = Start();

        session.Pause(PauseReason.User);
        session.Resume();

        Assert.False(session.PausedByUser);
        Assert.False(session.IsPaused);
    }

    // Next and Skip are reachable from the rail while the pause sheet is up. Both start a pose, and
    // a pose that has just started is not paused — the sheet must never be left over a running one.
    [Fact]
    public void Next_ClearsTheUserPause()
    {
        var (session, _) = Start();

        session.Pause(PauseReason.User);
        session.Next();

        Assert.False(session.PausedByUser);
        Assert.False(session.IsPaused);
    }

    [Fact]
    public void Skip_ClearsTheUserPause()
    {
        var (session, _) = Start();

        session.Pause(PauseReason.User);
        session.Skip();

        Assert.False(session.PausedByUser);
        Assert.False(session.IsPaused);
    }

    // The sheet can never be raised over the summary: a completed session ignores both commands.
    [Fact]
    public void UserPause_AfterCompletion_IsANoOp()
    {
        var (session, _) = Start(30, count: 1);

        session.Next();
        Assert.True(session.IsComplete);

        session.Pause(PauseReason.User);

        Assert.False(session.PausedByUser);
    }

    // Every new image gets the full time back (INV-POSE-2, INV-CD-6).
    [Fact]
    public void AdvancingGivesTheNextPoseTheFullDuration()
    {
        var (session, clock) = Start(30);

        clock.Advance(30);
        Assert.True(session.IsExpired);

        session.Next();

        Assert.False(session.IsExpired);
        Assert.True(session.IsRunning);
        Assert.Equal(TimeSpan.FromSeconds(30), session.TimeRemaining);

        clock.Advance(7);
        Assert.Equal(TimeSpan.FromSeconds(23), session.TimeRemaining);
    }

    [Fact]
    public void AdvancingAlsoUnpausesTheClock()
    {
        var (session, clock) = Start(30);

        session.Pause();
        clock.Advance(3);
        session.Next();
        clock.Advance(2);

        Assert.False(session.IsPaused);
        Assert.Equal(TimeSpan.FromSeconds(28), session.TimeRemaining);
    }

    // A break runs its own, differently sized clock — the phase duration follows the phase.
    [Fact]
    public void EnteringABreak_SwitchesTheClockToTheBreakDuration()
    {
        var (session, _) = Start(30, breakSeconds: 120);

        session.Next();

        Assert.True(session.OnBreak);
        Assert.Equal(TimeSpan.FromSeconds(120), session.PhaseDuration);
        Assert.Equal("2:00", session.Display);
        Assert.Equal(100, session.RemainingPercent);   // the ring measures the rest, not the pose
    }

    [Fact]
    public void LeavingABreak_RestoresThePoseDuration()
    {
        var (session, clock) = Start(30, breakSeconds: 15);

        session.Next();            // into the break
        clock.Advance(15);
        Assert.Equal(SessionTick.PoseStarted, session.Tick());

        Assert.False(session.OnBreak);
        Assert.Equal(TimeSpan.FromSeconds(30), session.PhaseDuration);
        Assert.Equal(100, session.RemainingPercent);
    }

    // A zero/negative configured duration must not produce a countdown that runs backwards forever.
    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void NonPositiveDuration_StartsExpired(int seconds)
    {
        var (session, _) = Start(seconds);

        Assert.True(session.IsExpired);
        Assert.False(session.IsRunning);
        Assert.Equal("0:00", session.Display);
        Assert.Equal(0, session.RemainingPercent);
    }
}
