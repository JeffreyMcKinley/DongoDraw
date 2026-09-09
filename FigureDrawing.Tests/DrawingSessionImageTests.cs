using FigureDrawing.Core;

namespace FigureDrawing.Tests;

// Resolving the current image id to something displayable, and the unreadable-image policy
// (INV-PLY-*): a broken file skips its pose without counting it, and a wholly undisplayable pool
// ends the session with a distinguishable error rather than looping. Uses string as the image type
// so a loader is just "id -> id, or null".
public class DrawingSessionImageTests
{
    static readonly string[] Pool = ["a", "b", "c", "d", "e"];

    // A session over Pool with a deterministic seed/clock so sequences are reproducible.
    static DrawingSession<string> Session(
        int count,
        Func<string, string?> load,
        IReadOnlyList<string>? pool = null,
        bool shuffle = false,
        Action<string>? onUnreadable = null,
        int maxConsecutiveFailures = 100) =>
        new(pool ?? Pool, new SessionConfig(30, count), load, shuffle, new Random(1234),
            () => TimeSpan.Zero, onUnreadable, maxConsecutiveFailures);

    // A loader where the given ids fail to load (return null); everything else loads to itself.
    static Func<string, string?> LoaderFailing(params string[] broken)
    {
        var set = broken.ToHashSet();
        return id => set.Contains(id) ? null : id;
    }

    [Fact]
    public void ShowsFirstImage_WhenItLoads()
    {
        var session = Session(count: 3, LoaderFailing());

        Assert.Equal("a", session.CurrentImage);   // pool order, first image loads
        Assert.Equal("a", session.CurrentImageId);
        Assert.False(session.IsComplete);
        Assert.False(session.CouldNotDisplayImage);
    }

    [Fact]
    public void SkipsUnreadableImages_UntilOneLoads_AndLogsEach()
    {
        var skipped = new List<string>();
        var session = Session(
            count: 3,                          // order a, b, c, ...
            LoaderFailing("a", "b"),           // a and b are broken
            onUnreadable: skipped.Add);

        Assert.Equal("c", session.CurrentImage);         // skipped past a and b
        Assert.Equal(new[] { "a", "b" }, skipped);       // both logged, in order
        Assert.False(session.CouldNotDisplayImage);
    }

    [Fact]
    public void SkippingBrokenImages_DoesNotCountThemTowardTheTotal()
    {
        // a broken, b good: the session lands on b having skipped a. b has not been completed yet.
        var session = Session(count: 3, LoaderFailing("a"));

        Assert.Equal("b", session.CurrentImage);
        Assert.Equal(0, session.ImagesDisplayed);   // an unreadable image never counts (INV-PLY-2)
        Assert.Equal(1, session.SkippedCount);
    }

    [Fact]
    public void AllImagesUnreadable_EndsSessionAndFlagsIt_WithinFailureBudget()
    {
        var skipped = new List<string>();
        var session = Session(
            count: 5,
            LoaderFailing("x", "y"),                    // every image broken
            pool: ["x", "y"],                           // pool < count -> repeats
            onUnreadable: skipped.Add,
            maxConsecutiveFailures: 10);

        Assert.True(session.IsComplete);
        Assert.True(session.CouldNotDisplayImage);
        Assert.Null(session.CurrentImage);
        Assert.Equal(10, skipped.Count);                 // gave up after the budget, no infinite loop
    }

    // Time spent failing to decode is not drawing time. Each attempt is a real decode on a device,
    // so an exhausted budget could otherwise bank seconds against a pose nobody ever saw
    // (INV-PLY-2, INV-SUM-3).
    [Fact]
    public void ExhaustingTheFailureBudget_BanksNoDrawingTime()
    {
        var now = TimeSpan.Zero;
        var session = new DrawingSession<string>(
            ["x", "y"],
            new SessionConfig(30, 5),
            _ =>
            {
                now += TimeSpan.FromSeconds(1);   // every attempt costs a second
                return null;
            },
            shuffle: false,
            random: new Random(1),
            clock: () => now,
            maxConsecutiveFailures: 10);

        Assert.True(session.IsComplete);
        Assert.True(session.CouldNotDisplayImage);
        Assert.Equal(0, session.ImagesDisplayed);
        Assert.Equal(TimeSpan.Zero, session.TotalDrawingTime);
        Assert.Equal(TimeSpan.Zero, session.AveragePoseTime);
    }

    [Fact]
    public void Next_CountsCurrentImage_AndAdvancesToNextDisplayable()
    {
        var session = Session(count: 3, LoaderFailing());
        Assert.Equal("a", session.CurrentImage);

        session.Next();
        Assert.Equal("b", session.CurrentImage);
        Assert.Equal(1, session.ImagesDisplayed);
    }

    [Fact]
    public void Next_SkipsBrokenImagesWhenAdvancing()
    {
        // a good, b broken, c good: completing a should land on c (b skipped).
        var session = Session(count: 3, LoaderFailing("b"));
        Assert.Equal("a", session.CurrentImage);

        session.Next();
        Assert.Equal("c", session.CurrentImage);
        Assert.Equal(1, session.ImagesDisplayed);   // only a counted; b was skipped
    }

    [Fact]
    public void CompletingConfiguredCount_EndsSession_WithoutErrorFlag()
    {
        var session = Session(count: 2, LoaderFailing());

        session.Next();
        session.Next();

        Assert.True(session.IsComplete);
        Assert.False(session.CouldNotDisplayImage);   // normal completion, not an image failure
        Assert.Null(session.CurrentImage);
        Assert.Equal(2, session.ImagesDisplayed);
    }

    [Fact]
    public void End_StopsImmediately_AndClearsCurrentImage()
    {
        var session = Session(count: 5, LoaderFailing());
        session.Next();                               // 1 completed

        session.End();

        Assert.True(session.IsComplete);
        Assert.Null(session.CurrentImage);
        Assert.Null(session.CurrentImageId);
        Assert.Equal(1, session.ImagesDisplayed);     // ended image not counted
    }

    [Fact]
    public void EmptyPool_CompletesImmediately_WithoutErrorFlag()
    {
        var session = Session(count: 3, LoaderFailing(), pool: []);

        Assert.True(session.IsComplete);
        Assert.Null(session.CurrentImage);
        Assert.False(session.CouldNotDisplayImage);   // nothing to display != failed to display
    }

    // INV-PLY-5 ("the loader never throws through the session") is a contract on the *adapter*: the
    // Android loader catches decode failures and returns null. The session does not paper over a
    // loader that throws, and `SessionScreenContractTests` is what guards the adapter's side.
    [Fact]
    public void NullPoolOrLoader_Throws()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new DrawingSession<string>(null!, new SessionConfig(30, 1), LoaderFailing()));
        Assert.Throws<ArgumentNullException>(() =>
            new DrawingSession<string>(Pool, new SessionConfig(30, 1), null!));
    }

    // The screen decodes the next pose while the current one is up, which it can only do if asking
    // is free. A peek that advanced the sequence, started a clock or counted anything would be a
    // query that mutates (INV-SES-1, INV-X-12).
    [Fact]
    public void UpcomingImageId_ReportsTheNextId_WithoutAdvancing()
    {
        var session = Session(count: 3, LoaderFailing());

        Assert.Equal("b", session.UpcomingImageId);      // pool order: a is up, b is next

        Assert.Equal("a", session.CurrentImageId);
        Assert.Equal("a", session.CurrentImage);
        Assert.Equal(0, session.CompletedCount);
        Assert.Equal(0, session.SkippedCount);
        Assert.Equal(TimeSpan.Zero, session.TotalDrawingTime);
        Assert.Equal(SessionPhase.Pose, session.Phase);
        Assert.Equal("0:30", session.Display);
    }

    // Refilling a drained pass is the one mutation the peek is allowed: a pass is materialised
    // whole, so it makes no difference whether the refill happens here or at the Advance that
    // follows (INV-SES-4).
    [Fact]
    public void UpcomingImageId_RefillsADrainedPass()
    {
        // Pool of two, count of four: the pass is drained after the second pose.
        var session = Session(count: 4, LoaderFailing(), pool: ["x", "y"]);
        session.Next();

        Assert.Equal("y", session.CurrentImageId);       // last id of the pass is on screen

        Assert.Equal("x", session.UpcomingImageId);      // the next pass is built to answer
        Assert.Equal("y", session.CurrentImageId);       // and the current pose did not move
    }

    // A completed session has nothing to decode ahead, and the screen binds its prefetch to this
    // answer — a non-null id here would have it decode a pose the summary screen never shows.
    [Fact]
    public void UpcomingImageId_IsNullOnceComplete()
    {
        var session = Session(count: 1, LoaderFailing());
        session.Next();

        Assert.True(session.IsComplete);
        Assert.Null(session.UpcomingImageId);
    }

    // A draft has no pool and no loader; every other command on the aggregate states the draft case
    // explicitly rather than relying on the count happening to be zero (INV-SET-4).
    [Fact]
    public void UpcomingImageId_IsNullForADraft() =>
        Assert.Null(DrawingSession<string>.Evaluate("30", "12", folderSelected: true).UpcomingImageId);

    // Remaining at 1 does not mean "no further image": a skip does not count a pose, so the session
    // can still show another one. The query names what comes next in the sequence, and leaves what
    // that costs to the counters (INV-PLY-7).
    [Fact]
    public void UpcomingImageId_OnTheLastPose_StillReportsAnId()
    {
        var session = Session(count: 1, LoaderFailing());

        Assert.Equal(1, session.Remaining);
        Assert.Equal("b", session.UpcomingImageId);
    }

    // The screen decodes whatever this names, and the session skips a known-unreadable id without
    // asking the loader at all — so naming one would spend a background decode on a file that never
    // reaches the screen and leave its replacement to be decoded on the boundary tick
    // (INV-PLY-7 with INV-PLY-8).
    [Fact]
    public void UpcomingImageId_SkipsIdsAlreadyKnownUnreadable()
    {
        // Pool of two with x broken, count 4: x comes round on every pass.
        var session = Session(count: 4, LoaderFailing("x"), pool: ["x", "y"]);

        Assert.Equal("y", session.CurrentImageId);       // x was skipped on the way in
        Assert.Equal("y", session.UpcomingImageId);      // x is known broken, so it is passed over
    }

    // The peek draws from the same injected Random as the passes it refills, so a screen that asks
    // must not shift the order a screen that never asks would have seen (INV-SES-4, INV-X-8).
    [Fact]
    public void Peeking_DoesNotChangeTheShuffledSequence()
    {
        var peeked = new List<string>();
        var plain = new List<string>();

        var withPeek = Session(count: 8, LoaderFailing(), shuffle: true);
        var without = Session(count: 8, LoaderFailing(), shuffle: true);

        while (!withPeek.IsComplete)
        {
            peeked.Add(withPeek.CurrentImageId!);
            _ = withPeek.UpcomingImageId;               // the only difference between the two runs
            withPeek.Next();
        }

        while (!without.IsComplete)
        {
            plain.Add(without.CurrentImageId!);
            without.Next();
        }

        Assert.Equal(plain, peeked);
    }

    // A pool smaller than the count repeats by design, so without this the screen pays a real decode
    // for the same broken file on every pass. The skip still happens — the session only stops
    // asking the loader, it does not stop counting (INV-PLY-2).
    [Fact]
    public void AnUnreadableId_IsNotLoadedTwice()
    {
        var attempts = new List<string>();
        var session = Session(
            count: 4,
            id =>
            {
                attempts.Add(id);
                return id == "x" ? null : id;
            },
            pool: ["x", "y"]);                          // x is broken and comes round every pass

        Assert.Equal(new[] { "x", "y" }, attempts);      // x tried once, y loaded
        Assert.Equal(1, session.SkippedCount);

        session.Next();                                 // y done -> x comes round again

        Assert.Equal(new[] { "x", "y", "y" }, attempts); // x not tried a second time
        Assert.Equal(2, session.SkippedCount);           // but it was still skipped past
    }

    // The budget still ends a hopeless run, but the cost of reaching that end is now one decode per
    // distinct id rather than one per failure — the difference between a folder-sized stall and a
    // pool-sized one on the thread that has to stay responsive (INV-PLY-8 bounding INV-PLY-3).
    [Fact]
    public void AWhollyUnreadablePool_IsLoadedOncePerId_NotOncePerFailure()
    {
        var attempts = new List<string>();
        var session = Session(
            count: 5,
            id => { attempts.Add(id); return null; },
            pool: ["x", "y"],
            maxConsecutiveFailures: 10);

        Assert.Equal(new[] { "x", "y" }, attempts);      // ten failures, two loads
    }

    // The failure budget is the player's to choose, and it chooses twice the pool. A run of failures
    // can span a pass boundary, so a budget of exactly the pool size could end a session whose pool
    // still holds a drawable image; twice the pool cannot (INV-PLY-3, INV-PLY-4).
    [Fact]
    public void ABudgetOfTwiceThePool_NeverEndsARunThatStillHasADrawableImage()
    {
        string[] pool = ["a", "b", "c", "d", "e", "f"];
        var session = Session(
            count: 4,
            LoaderFailing("a", "b", "c", "d", "e"),     // only f is drawable
            pool: pool,
            shuffle: true,
            maxConsecutiveFailures: pool.Length * 2);

        while (!session.IsComplete)
            session.Next();

        Assert.False(session.CouldNotDisplayImage);      // never a false "could not display"
        Assert.Equal(4, session.ImagesDisplayed);
    }

    // The tick that exhausts the budget ends the session, so it reports completion like any other
    // ending tick — reporting a new pose there would chime and repaint for a run that is over
    // (INV-SES-13 meeting INV-PLY-3).
    [Fact]
    public void TheTickThatExhaustsTheFailureBudget_ReportsCompletion()
    {
        var now = TimeSpan.Zero;
        var broken = false;
        var session = new DrawingSession<string>(
            ["a", "x", "y"],
            new SessionConfig(30, 5),
            id => broken || id != "a" ? null : id,   // "a" loads until the folder goes away
            shuffle: false,
            random: new Random(1),
            clock: () => now,
            maxConsecutiveFailures: 4);

        Assert.Equal("a", session.CurrentImage);         // the one drawable image is up

        broken = true;
        now += TimeSpan.FromSeconds(30);

        Assert.Equal(SessionTick.Completed, session.Tick());
        Assert.True(session.CouldNotDisplayImage);
    }
}
