using FigureDrawing.Core;

namespace FigureDrawing.Tests;

// Cross-context end-to-end: a whole session wired through the REAL objects the screens use —
// the reference library enumerates a picked folder, the setup draft produces the config, and the
// session aggregate runs it start-to-summary (sequence, pose clock, image resolution, totals). No
// Android/Appium here; this drives the same public surface SessionActivity drives.
//
// The Screen harness below mirrors that Activity's loop exactly (repaint, advance at zero,
// pause/resume on lifecycle), so a break in the wiring shows up here instead of only on a device.
public class SessionE2ETests
{
    // In-memory document tree so the reference library runs for real against a picked "folder".
    sealed class FakeTree(Dictionary<string, DocumentEntry[]> children) : IDocumentTree
    {
        public IEnumerable<DocumentEntry> GetChildren(string parentDocumentId) =>
            children.TryGetValue(parentDocumentId, out var kids) ? kids : [];
    }

    sealed class FakeClock
    {
        public TimeSpan Now { get; private set; }

        public Func<TimeSpan> Read => () => Now;

        public void Advance(TimeSpan by) => Now += by;

        public void Advance(double seconds) => Now += TimeSpan.FromSeconds(seconds);
    }

    // Stand-in for SessionActivity: same state machine, no Android.
    sealed class Screen
    {
        public Screen(IReadOnlyList<string> pool, SessionConfig config, Func<TimeSpan> clock,
                      Func<string, string?>? load = null)
        {
            Session = new DrawingSession<string>(
                pool, config, load ?? (id => id), shuffle: false, random: new Random(3), clock: clock);

            Render();
        }

        public DrawingSession<string> Session { get; }

        public bool Ticking { get; private set; }

        // The pause sheet, which the screen binds to the session's own reason rather than to
        // IsPaused — a lifecycle pause stops the clocks without covering the pose (INV-CD-8).
        public bool PauseSheetVisible => Session.PausedByUser;

        // How many times the screen would have played the pose-change tone. Only an automatic
        // change into a POSE chimes: a rest starting is not a new pose, and a finished session is
        // not a pose at all.
        public int Chimes { get; private set; }

        public TimeSpan TickInterval { get; } = TimeSpan.FromMilliseconds(200);

        // What the timer view currently reads, recorded on every repaint.
        public List<string> DisplayedTimes { get; } = [];

        // Each image as it appeared on screen, in order.
        public List<string> DisplayedImages { get; } = [];

        // One pass of the Handler loop: repaint, then let the session expire the phase if its time
        // ran out. The screen decides nothing — the session says which transition it made, and the
        // tone follows that rather than being reconstructed from the state afterwards (INV-SES-13).
        public void Tick()
        {
            if (!Ticking)
                return;

            DisplayedTimes.Add(Session.Display);

            switch (Session.Tick())
            {
                case SessionTick.PoseStarted:
                    Chimes++;
                    Render();
                    break;

                case SessionTick.BreakStarted:
                case SessionTick.Completed:
                    Render();
                    break;
            }
        }

        // A manual "done" tap: count the pose. The session hands the next one a full clock itself.
        public void Advance()
        {
            if (Session.IsComplete)
            {
                Ticking = false;
                return;
            }

            Session.Next();
            Render();
        }

        // The rail's Skip. Reachable while the pause sheet is up — the sheet covers the stage, not
        // the rail — so it is also the path that must not leave the sheet over a running pose.
        public void Skip()
        {
            if (Session.IsComplete)
                return;

            Session.Skip();
            Render();
        }

        // The pause sheet's own button.
        public void UserPause()
        {
            if (Session.IsComplete)
                return;

            Session.Pause(PauseReason.User);
            Ticking = false;
            Render();
        }

        public void UserResume()
        {
            if (Session.IsComplete)
                return;

            Session.Resume();
            Render();
        }

        public void OnPause()
        {
            Session.Pause();
            Ticking = false;
        }

        // Foregrounded again. A session that is over, or that the drawer paused deliberately, stays
        // as it is — coming back from the background must not restart a pose nobody resumed.
        public void OnResume()
        {
            if (Session.IsComplete || Session.PausedByUser)
                return;

            Session.Resume();
            Ticking = true;
        }

        void Render()
        {
            if (Session.CurrentImage is { } id)
            {
                // The next pose's image is already loaded when a break starts, so the break's own
                // exit must not record it a second time.
                if (DisplayedImages.Count == 0 || DisplayedImages[^1] != id)
                    DisplayedImages.Add(id);

                // The screen only restarts its repaint loop for a session whose clocks are running.
                Ticking = !Session.IsPaused;
                return;
            }

            Ticking = false;
        }
    }

    const string Dir = ReferenceLibrary.DirectoryMimeType;

    static IReadOnlyList<string> Pool(params string[] names)
    {
        var tree = new FakeTree(new()
        {
            ["root"] = names.Select(n => new DocumentEntry($"root/{n}", "image/jpeg")).ToArray(),
        });

        return new ReferenceLibrary(tree, "root").Pool;
    }

    // Run the screen's loop for a wall-clock span, ticking at the screen's repaint interval.
    static void Run(Screen screen, FakeClock clock, TimeSpan duration)
    {
        for (var elapsed = TimeSpan.Zero; elapsed < duration; elapsed += screen.TickInterval)
        {
            clock.Advance(screen.TickInterval);
            screen.Tick();
        }
    }

    // --- Whole sessions -------------------------------------------------------

    [Fact]
    public void FullSession_FromPickedFolder_ProducesExpectedSummary()
    {
        // 1) Enumerate a picked folder (root + one subfolder, mixed files).
        var tree = new FakeTree(new()
        {
            ["root"] =
            [
                new DocumentEntry("root/pose1.jpg", "image/jpeg"),
                new DocumentEntry("root/pose2.png", "image/png"),
                new DocumentEntry("root/notes.txt", "text/plain"),   // ignored
                new DocumentEntry("root/more", Dir),
            ],
            ["root/more"] =
            [
                new DocumentEntry("root/more/pose3.webp", "image/webp"),
                new DocumentEntry("root/more/pose4.jpg", "image/jpeg"),
            ],
        });

        var pool = new ReferenceLibrary(tree, "root").Pool;
        Assert.Equal(4, pool.Count);   // 4 images, .txt excluded

        // 2) Validate the user's setup inputs into a config.
        var draft = DrawingSession<string>.Evaluate("30", "6", folderSelected: true);
        Assert.True(draft.CanStart);
        var config = draft.Config!.Value;   // 30s/image, 6 images (> pool -> repeats)

        // 3) Run the session as the screen would, deterministic clock + seed.
        var clock = new FakeClock();
        var session = new DrawingSession<string>(
            pool, config, id => id, shuffle: true, random: new Random(2026), clock: clock.Read);

        var displayed = new List<string>();

        // Complete images 1-2 on the 30s timer.
        for (var i = 0; i < 2; i++)
        {
            displayed.Add(session.CurrentImage!);
            clock.Advance(30);
            session.Next();
        }

        // Skip image 3 after 8s (does not count, time excluded).
        var skipped = session.CurrentImage!;
        clock.Advance(8);
        session.Skip();
        Assert.NotEqual(skipped, session.CurrentImage);

        // Complete images 3-6 on the timer to reach the configured count of 6.
        while (!session.IsComplete)
        {
            displayed.Add(session.CurrentImage!);
            clock.Advance(30);
            session.Next();
        }

        // 4) The summary: 6 images completed, 6*30s drawn, the skipped 8s excluded.
        Assert.True(session.IsComplete);
        Assert.Equal(6, session.ImagesDisplayed);
        Assert.Equal(TimeSpan.FromSeconds(6 * 30), session.TotalDrawingTime);

        Assert.Equal(6, displayed.Count);
        Assert.All(displayed, img => Assert.Contains(img, pool));
    }

    [Fact]
    public void FullSession_EndedEarly_SummaryReflectsWorkSoFar()
    {
        var tree = new FakeTree(new()
        {
            ["root"] =
            [
                new DocumentEntry("root/a.jpg", "image/jpeg"),
                new DocumentEntry("root/b.jpg", "image/jpeg"),
                new DocumentEntry("root/c.jpg", "image/jpeg"),
            ],
        });
        var pool = new ReferenceLibrary(tree, "root").Pool;

        var config = DrawingSession<string>.Evaluate("20", "10", folderSelected: true).Config!.Value;
        var clock = new FakeClock();
        var session = new DrawingSession<string>(
            pool, config, id => id, shuffle: false, random: new Random(1), clock: clock.Read);

        clock.Advance(20);
        session.Next();          // 1 completed, 20s
        clock.Advance(20);
        session.Next();          // 2 completed, 40s
        clock.Advance(5);        // mid-third image
        session.End();           // bank partial, stop early

        Assert.True(session.IsComplete);
        Assert.Equal(2, session.ImagesDisplayed);                         // ended image not counted
        Assert.Equal(TimeSpan.FromSeconds(45), session.TotalDrawingTime); // 20 + 20 + 5 partial
    }

    [Fact]
    public void FullSession_FromPickedFolder_SkipsABrokenImage_AndCompletes()
    {
        var tree = new FakeTree(new()
        {
            ["root"] =
            [
                new DocumentEntry("root/pose1.jpg", "image/jpeg"),
                new DocumentEntry("root/pose2.png", "image/png"),
                new DocumentEntry("root/notes.txt", "text/plain"),   // ignored
                new DocumentEntry("root/pose3.webp", "image/webp"),
            ],
        });
        var pool = new ReferenceLibrary(tree, "root").Pool;
        Assert.Equal(3, pool.Count);

        // 3 images, matching the pool exactly. pose2 is "broken" (the loader returns null); the
        // session must skip past it without counting it and still reach the target of 3.
        var config = DrawingSession<string>.Evaluate("30", "3", folderSelected: true).Config!.Value;
        var skipped = new List<string>();
        var session = new DrawingSession<string>(
            pool, config, id => id.Contains("pose2") ? null : id,
            shuffle: false, random: new Random(7), clock: () => TimeSpan.Zero,
            onUnreadable: skipped.Add);

        var displayed = new List<string>();
        while (!session.IsComplete)
        {
            displayed.Add(session.CurrentImage!);
            session.Next();
        }

        // The broken image was skipped and logged; the session still completed its 3 images.
        Assert.Contains("root/pose2.png", skipped);
        Assert.DoesNotContain(displayed, d => d.Contains("pose2"));
        Assert.True(session.IsComplete);
        Assert.False(session.CouldNotDisplayImage);
        Assert.Equal(3, session.ImagesDisplayed);
    }

    [Fact]
    public void FullSession_AllImagesBroken_SurfacesCouldNotDisplay()
    {
        var tree = new FakeTree(new()
        {
            ["root"] =
            [
                new DocumentEntry("root/a.jpg", "image/jpeg"),
                new DocumentEntry("root/b.jpg", "image/jpeg"),
            ],
        });
        var pool = new ReferenceLibrary(tree, "root").Pool;
        var config = DrawingSession<string>.Evaluate("30", "5", folderSelected: true).Config!.Value;

        var session = new DrawingSession<string>(
            pool, config, _ => null, shuffle: false, random: new Random(1),
            clock: () => TimeSpan.Zero, maxConsecutiveFailures: 8);

        Assert.True(session.IsComplete);
        Assert.True(session.CouldNotDisplayImage);
        Assert.Null(session.CurrentImage);
        Assert.Equal(0, session.ImagesDisplayed);
    }

    // --- The screen's repaint loop --------------------------------------------

    // At zero the next image loads automatically and the count advances; the timer resets for each
    // new image; the whole session runs itself to completion.
    [Fact]
    public void TimedSession_AutoAdvancesEveryPose_AndCompletesOnItsOwn()
    {
        var clock = new FakeClock();
        var pool = Pool("a.jpg", "b.jpg", "c.jpg");
        var config = DrawingSession<string>.Evaluate("30", "3", folderSelected: true).Config!.Value;
        var screen = new Screen(pool, config, clock.Read);

        // One pose in: still on the first image, nothing counted yet.
        Run(screen, clock, TimeSpan.FromSeconds(29));
        Assert.Single(screen.DisplayedImages);
        Assert.Equal(0, screen.Session.ImagesDisplayed);

        // Cross the first expiry: second image, timer back to full.
        Run(screen, clock, TimeSpan.FromSeconds(1.4));
        Assert.Equal(2, screen.DisplayedImages.Count);
        Assert.Equal(1, screen.Session.ImagesDisplayed);
        Assert.Equal("0:30", screen.Session.Display);

        // Let the rest of the session run itself out (3 x 30s plus slack).
        Run(screen, clock, TimeSpan.FromSeconds(70));

        Assert.True(screen.Session.IsComplete);
        Assert.False(screen.Ticking);
        Assert.Equal(3, screen.DisplayedImages.Count);
        Assert.Equal(3, screen.Session.ImagesDisplayed);
        Assert.Equal(new[] { "root/a.jpg", "root/b.jpg", "root/c.jpg" }, screen.DisplayedImages);
    }

    // The countdown is visible and updates each second — every second from 30 down to 0 must
    // actually appear on screen, in descending order.
    [Fact]
    public void Countdown_ShowsEverySecond_InDescendingOrder()
    {
        var clock = new FakeClock();
        var config = DrawingSession<string>.Evaluate("30", "1", folderSelected: true).Config!.Value;
        var screen = new Screen(Pool("a.jpg"), config, clock.Read);

        Run(screen, clock, TimeSpan.FromSeconds(30));

        var seen = screen.DisplayedTimes.Distinct().ToList();
        Assert.Equal(
            Enumerable.Range(0, 31).Reverse().Select(DrawingSession.Format).ToList(),
            seen);
    }

    // Backgrounding pauses the timer — it must not drain or fire while hidden.
    [Fact]
    public void Backgrounding_PausesTheTimer_AndResumesWhereItLeftOff()
    {
        var clock = new FakeClock();
        var config = DrawingSession<string>.Evaluate("60", "2", folderSelected: true).Config!.Value;
        var screen = new Screen(Pool("a.jpg", "b.jpg"), config, clock.Read);

        Run(screen, clock, TimeSpan.FromSeconds(20));
        Assert.Equal("0:40", screen.Session.Display);

        // Home button: five minutes in the background, far longer than the pose.
        screen.OnPause();
        clock.Advance(TimeSpan.FromMinutes(5));

        Assert.False(screen.Ticking);
        Assert.Equal("0:40", screen.Session.Display);
        Assert.Single(screen.DisplayedImages);                       // did NOT advance while hidden
        Assert.Equal(0, screen.Session.ImagesDisplayed);

        // Back to the app: the pose resumes with its remaining 40s intact.
        screen.OnResume();
        Run(screen, clock, TimeSpan.FromSeconds(39));
        Assert.Single(screen.DisplayedImages);

        Run(screen, clock, TimeSpan.FromSeconds(1.4));
        Assert.Equal(2, screen.DisplayedImages.Count);
        Assert.Equal(1, screen.Session.ImagesDisplayed);
    }

    // A manual "done" tap mid-pose counts the image AND hands the next pose a full timer.
    [Fact]
    public void ManualAdvance_ResetsTheTimerForTheNextPose()
    {
        var clock = new FakeClock();
        var config = DrawingSession<string>.Evaluate("45", "3", folderSelected: true).Config!.Value;
        var screen = new Screen(Pool("a.jpg", "b.jpg", "c.jpg"), config, clock.Read);

        Run(screen, clock, TimeSpan.FromSeconds(5));
        screen.Advance();

        Assert.Equal(1, screen.Session.ImagesDisplayed);
        Assert.Equal("0:45", screen.Session.Display);

        // And the fresh timer still expires a full 45s later, not 40s.
        Run(screen, clock, TimeSpan.FromSeconds(44));
        Assert.Equal(2, screen.DisplayedImages.Count);
        Run(screen, clock, TimeSpan.FromSeconds(1.4));
        Assert.Equal(3, screen.DisplayedImages.Count);
    }

    // Unreadable images are skipped without the countdown handing them pose time: the broken image
    // never shows, and the poses that do show each get their full duration.
    [Fact]
    public void BrokenImages_DoNotConsumePoseTime()
    {
        var clock = new FakeClock();
        var config = DrawingSession<string>.Evaluate("30", "2", folderSelected: true).Config!.Value;
        var screen = new Screen(
            Pool("a.jpg", "broken.jpg", "c.jpg"),
            config,
            clock.Read,
            load: id => id.Contains("broken") ? null : id);

        Run(screen, clock, TimeSpan.FromSeconds(30.4));

        Assert.DoesNotContain(screen.DisplayedImages, d => d.Contains("broken"));
        Assert.Equal(new[] { "root/a.jpg", "root/c.jpg" }, screen.DisplayedImages);
        Assert.Equal("0:30", screen.Session.Display);

        Run(screen, clock, TimeSpan.FromSeconds(30.4));
        Assert.True(screen.Session.IsComplete);
        Assert.Equal(2, screen.Session.ImagesDisplayed);
    }

    // --- Pausing on purpose, across the lifecycle (INV-CD-8) -------------------

    // The flow the pause reason exists for: the drawer pauses, puts the phone down, the app is
    // backgrounded and comes back. The pose must still be stopped, the sheet still up, and no time
    // burned — only an explicit resume restarts it.
    [Fact]
    public void AUserPause_SurvivesBackgroundingAndReturning()
    {
        var clock = new FakeClock();
        var config = DrawingSession<string>.Evaluate("60", "2", folderSelected: true).Config!.Value;
        var screen = new Screen(Pool("a.jpg", "b.jpg"), config, clock.Read);

        Run(screen, clock, TimeSpan.FromSeconds(20));
        Assert.Equal("0:40", screen.Session.Display);

        screen.UserPause();
        Assert.True(screen.PauseSheetVisible);
        Assert.False(screen.Ticking);

        // Backgrounded for two minutes with the sheet up, then foregrounded.
        screen.OnPause();
        clock.Advance(TimeSpan.FromMinutes(2));
        screen.OnResume();

        Assert.True(screen.Session.IsPaused);
        Assert.True(screen.PauseSheetVisible);
        Assert.False(screen.Ticking);
        Assert.Equal("0:40", screen.Session.Display);
        Assert.Single(screen.DisplayedImages);

        // Only the drawer's own resume restarts it, with the pose intact.
        screen.UserResume();
        Assert.False(screen.PauseSheetVisible);
        Assert.True(screen.Ticking);

        Run(screen, clock, TimeSpan.FromSeconds(39));
        Assert.Single(screen.DisplayedImages);
        Run(screen, clock, TimeSpan.FromSeconds(1.4));
        Assert.Equal(2, screen.DisplayedImages.Count);
    }

    // A lifecycle pause is not the drawer's: returning resumes the pose and the sheet never shows.
    [Fact]
    public void ALifecyclePause_DoesNotRaiseThePauseSheet()
    {
        var clock = new FakeClock();
        var config = DrawingSession<string>.Evaluate("60", "2", folderSelected: true).Config!.Value;
        var screen = new Screen(Pool("a.jpg", "b.jpg"), config, clock.Read);

        Run(screen, clock, TimeSpan.FromSeconds(10));
        screen.OnPause();

        Assert.True(screen.Session.IsPaused);
        Assert.False(screen.PauseSheetVisible);

        screen.OnResume();

        Assert.False(screen.Session.IsPaused);
        Assert.True(screen.Ticking);
    }

    // The rail stays live under the sheet, so its commands must take the sheet down with them —
    // otherwise the drawer watches a frozen sheet over a pose whose clock is draining.
    [Theory]
    [InlineData(true)]   // rail Next
    [InlineData(false)]  // rail Skip
    public void ARailCommandDuringAUserPause_TakesTheSheetDown(bool useNext)
    {
        var clock = new FakeClock();
        var config = DrawingSession<string>.Evaluate("30", "3", folderSelected: true).Config!.Value;
        var screen = new Screen(Pool("a.jpg", "b.jpg", "c.jpg"), config, clock.Read);

        Run(screen, clock, TimeSpan.FromSeconds(5));
        screen.UserPause();
        Assert.True(screen.PauseSheetVisible);

        if (useNext)
            screen.Advance();
        else
            screen.Skip();

        Assert.False(screen.PauseSheetVisible);
        Assert.False(screen.Session.IsPaused);
        Assert.True(screen.Ticking);
        Assert.Equal("0:30", screen.Session.Display);
    }

    // --- The pose-change tone -------------------------------------------------

    // Only a change INTO a pose chimes. With a break configured that is one tone per boundary, not
    // two, and the tone at a break's start would announce a pose the drawer cannot see yet.
    [Fact]
    public void WithBreaks_OnlyTheBreaksExitChimes()
    {
        var clock = new FakeClock();
        var config = DrawingSession<string>.Evaluate("30", "2", folderSelected: true, breakSeconds: 15)
            .Config!.Value;
        var screen = new Screen(Pool("a.jpg", "b.jpg"), config, clock.Read);

        Run(screen, clock, TimeSpan.FromSeconds(30.4));
        Assert.True(screen.Session.OnBreak);
        Assert.Equal(0, screen.Chimes);            // entering the rest is not a new pose

        Run(screen, clock, TimeSpan.FromSeconds(15.4));
        Assert.False(screen.Session.OnBreak);
        Assert.Equal(1, screen.Chimes);            // leaving it is

        Run(screen, clock, TimeSpan.FromSeconds(30.4));
        Assert.True(screen.Session.IsComplete);
        Assert.Equal(1, screen.Chimes);            // completion is not a pose change
    }

    // Without a break every expiry is a pose change, and the last one still completes silently.
    [Fact]
    public void WithoutBreaks_EveryPoseChangeChimes_ExceptTheLast()
    {
        var clock = new FakeClock();
        var config = DrawingSession<string>.Evaluate("30", "3", folderSelected: true).Config!.Value;
        var screen = new Screen(Pool("a.jpg", "b.jpg", "c.jpg"), config, clock.Read);

        Run(screen, clock, TimeSpan.FromSeconds(91.4));

        Assert.True(screen.Session.IsComplete);
        Assert.Equal(2, screen.Chimes);
    }

    // A manual done-tap does not chime: the drawer who tapped is already looking at the screen.
    [Fact]
    public void AManualAdvance_DoesNotChime()
    {
        var clock = new FakeClock();
        var config = DrawingSession<string>.Evaluate("30", "3", folderSelected: true).Config!.Value;
        var screen = new Screen(Pool("a.jpg", "b.jpg", "c.jpg"), config, clock.Read);

        Run(screen, clock, TimeSpan.FromSeconds(5));
        screen.Advance();

        Assert.Equal(1, screen.Session.ImagesDisplayed);
        Assert.Equal(0, screen.Chimes);
    }

    // A session with a rest between poses, run through the repaint loop: the break shows, it does
    // not count a pose, and the drawing total excludes it (INV-SES-10, INV-SES-12).
    [Fact]
    public void TimedSession_WithBreaks_RestsBetweenPosesWithoutBankingTheRest()
    {
        var clock = new FakeClock();
        var config = DrawingSession<string>.Evaluate("30", "2", folderSelected: true, breakSeconds: 15)
            .Config!.Value;
        var screen = new Screen(Pool("a.jpg", "b.jpg"), config, clock.Read);

        Run(screen, clock, TimeSpan.FromSeconds(30.4));
        Assert.True(screen.Session.OnBreak);
        Assert.Equal(1, screen.Session.ImagesDisplayed);

        Run(screen, clock, TimeSpan.FromSeconds(15.4));
        Assert.False(screen.Session.OnBreak);
        Assert.Equal(1, screen.Session.ImagesDisplayed);

        Run(screen, clock, TimeSpan.FromSeconds(30.4));
        Assert.True(screen.Session.IsComplete);
        Assert.Equal(2, screen.Session.ImagesDisplayed);
        Assert.Equal(TimeSpan.FromSeconds(60), screen.Session.TotalDrawingTime);   // the rest is not drawing

        // The image loaded under the break overlay is the one the drawer then works on — it shows
        // once, not once per phase change.
        Assert.Equal(new[] { "root/a.jpg", "root/b.jpg" }, screen.DisplayedImages);
    }
}
