using DongoDraw.Core;

namespace DongoDraw.Tests;

// Drawing time is the headline number on the summary screen, and the break makes it easy to get
// wrong: the session engine's clock keeps running through a rest, so a naive wiring banks the break
// as time spent drawing. These tests pin the arithmetic for a whole run.
public class DrawingSessionTimeAccountingTests
{
    sealed class FakeClock
    {
        public TimeSpan Now;
        public Func<TimeSpan> Read => () => Now;
        public void Advance(double seconds) => Now += TimeSpan.FromSeconds(seconds);
    }

    static readonly string[] Pool = { "a", "b", "c", "d" };

    static DrawingSession<string> Make(FakeClock clock, int seconds, int count, int breakSeconds)
        =>
        new(Pool, new SessionConfig(seconds, count, breakSeconds), id => id,
            shuffle: false, random: new Random(1), clock: clock.Read);

    // Two 30s poses with a 5s rest take 65s of wall time, but only 60s of them are drawing.
    [Fact]
    public void BreakTime_IsNotBankedAsDrawingTime()
    {
        var clock = new FakeClock();
        var s = Make(clock, seconds: 30, count: 2, breakSeconds: 5);

        clock.Advance(30);
        s.Tick();               // pose 1 complete -> break
        clock.Advance(5);
        s.Tick();               // break over -> pose 2
        clock.Advance(30);
        s.Tick();               // pose 2 complete -> session over

        Assert.True(s.IsComplete);
        Assert.Equal(2, s.ImagesDisplayed);
        Assert.Equal(TimeSpan.FromSeconds(60), s.TotalDrawingTime);
        Assert.Equal(TimeSpan.FromSeconds(30), s.AveragePoseTime);
    }

    // The same run without a rest: nothing to exclude, so wall time and drawing time agree.
    [Fact]
    public void WithoutABreak_DrawingTimeIsTheWholeRun()
    {
        var clock = new FakeClock();
        var s = Make(clock, seconds: 30, count: 2, breakSeconds: 0);

        clock.Advance(30);
        s.Tick();
        clock.Advance(30);
        s.Tick();

        Assert.Equal(TimeSpan.FromSeconds(60), s.TotalDrawingTime);
    }

    // A long rest must not inflate the average pose either — that number is what tells a drawer
    // whether they are actually working at the pace they set.
    [Fact]
    public void ALongBreak_DoesNotInflateTheAveragePose()
    {
        var clock = new FakeClock();
        var s = Make(clock, seconds: 60, count: 3, breakSeconds: 60);

        for (var pose = 0; pose < 3; pose++)
        {
            clock.Advance(60);
            s.Tick();           // pose complete -> break (or the end, after the last one)

            if (!s.IsComplete)
            {
                clock.Advance(60);
                s.Tick();       // break over -> next pose
            }
        }

        Assert.True(s.IsComplete);
        Assert.Equal(TimeSpan.FromSeconds(180), s.TotalDrawingTime);
        Assert.Equal(TimeSpan.FromSeconds(60), s.AveragePoseTime);
    }

    // Ending mid-pose banks that pose's partial time but does not count it, and a rest the drawer
    // was sitting in when they ended banks nothing at all.
    [Fact]
    public void EndingDuringABreak_BanksNoBreakTime()
    {
        var clock = new FakeClock();
        var s = Make(clock, seconds: 30, count: 4, breakSeconds: 15);

        clock.Advance(30);
        s.Tick();               // pose 1 complete -> break
        clock.Advance(10);      // ten seconds into the rest
        s.End();

        Assert.Equal(1, s.ImagesDisplayed);
        Assert.Equal(TimeSpan.FromSeconds(30), s.TotalDrawingTime);
    }
}
