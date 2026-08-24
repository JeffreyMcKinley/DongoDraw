using OpenQA.Selenium;
using Xunit.Abstractions;

namespace FigureDrawing.UITests;

// FD-004 session player end-to-end over Appium/UiAutomator2. Opt-in via RUN_APPIUM=1 (see
// UiTestEnvironment) — otherwise every test skips-as-pass. Drives setup -> Start -> the full-screen
// player on a real emulator; the pure session logic is covered by DrawingSessionTests and its
// sibling files (image resolution, countdown, breaks).
//
// Every test here leaves the app on the player, which is a different Activity from the tabbed setup
// screen. Getting back is AppiumGuard.ReturnToMainScreen's job (OpenTab calls it).
[Collection(AppiumCollection.Name)]
public class SessionPlayerUiTests(AppiumAppFixture app, ITestOutputHelper output)
{
    AppiumGuard? Ready()
    {
        if (!UiTestEnvironment.Enabled)
        {
            output.WriteLine("Skipped: set RUN_APPIUM=1 (and boot emulator + Appium) to run.");
            return null;
        }

        Assert.True(app.StartupError is null, $"Appium session failed to start: {app.StartupError}");
        Assert.NotNull(app.Driver);
        return new AppiumGuard(app.Driver!);
    }

    // Selecting a folder with images and tapping Start opens the player showing one full-screen image.
    [Fact]
    public void StartingSession_ShowsFullScreenImage()
    {
        if (Ready() is not { } g) return;

        StartSession(g, imageCount: 3);

        var image = g.WaitForId("session_image", TimeSpan.FromSeconds(10));
        Assert.NotNull(image);
        Assert.True(image!.Displayed, "Session image should be visible after Start.");
        // The setup inputs belong to the previous screen — they must be gone on the player.
        Assert.Empty(g.FindAllById("start_button"));
    }

    // FD-005: the countdown is visible on the player and actually counts down each second.
    [Fact]
    public void SessionPlayer_ShowsACountdown_ThatTicksDown()
    {
        if (Ready() is not { } g) return;

        // A long pose, so the tick being observed can't be confused with the pose rolling over.
        StartSession(g, imageCount: 3, seconds: 60);

        var timer = g.WaitForId("session_timer", TimeSpan.FromSeconds(10));
        Assert.NotNull(timer);
        Assert.True(timer!.Displayed, "Countdown should be visible during a pose.");

        var first = timer.Text;
        Assert.Matches(@"^\d+:\d{2}$", first);

        // Within a few seconds the displayed value must have changed (and gone DOWN).
        var changed = g.WaitUntil(
            _ => g.FindById("session_timer").Text != first,
            TimeSpan.FromSeconds(5));

        Assert.True(changed, $"Countdown stayed at {first}; it should update each second.");
        Assert.True(Seconds(g.FindById("session_timer").Text) < Seconds(first),
            "Countdown should decrease, not increase.");
    }

    // FD-005: at zero the next pose loads automatically, with no user input at all. Uses the
    // shortest usable pose length so the test doesn't wait out a default 30s pose, and no break, so
    // what is being timed is the pose rolling over rather than a rest in between.
    [Fact]
    public void CountdownExpiry_AdvancesToTheNextPose_WithoutInput()
    {
        if (Ready() is not { } g) return;

        StartSession(g, imageCount: 3, seconds: 3);

        var timer = g.WaitForId("session_timer", TimeSpan.FromSeconds(10));
        Assert.NotNull(timer);

        // Wait for the timer to wrap back up to a fresh pose (it can only go up by restarting).
        var restarted = g.WaitUntil(_ => Seconds(g.FindById("session_timer").Text) >= 3,
            TimeSpan.FromSeconds(12));

        Assert.True(restarted, "Countdown should reset for the next pose once it reaches zero.");
        Assert.Equal(UiTestEnvironment.AppPackage, g.Driver.CurrentPackage);
        Assert.True(g.WaitForId("session_image", TimeSpan.FromSeconds(5))?.Displayed == true,
            "The next image should be showing after auto-advance.");
    }

    // FD-005: backgrounding the app must freeze the countdown — it may not drain (or fire) while
    // the app is hidden.
    [Fact]
    public void Backgrounding_PausesTheCountdown()
    {
        if (Ready() is not { } g) return;

        StartSession(g, imageCount: 3, seconds: 60);

        Assert.NotNull(g.WaitForId("session_timer", TimeSpan.FromSeconds(10)));
        var before = Seconds(g.FindById("session_timer").Text);

        g.Driver.BackgroundApp(TimeSpan.FromSeconds(6));
        g.WaitUntil(d => d.CurrentPackage == UiTestEnvironment.AppPackage, TimeSpan.FromSeconds(15));

        var after = Seconds(g.FindById("session_timer").Text);

        // A running timer would have lost ~6s; a paused one loses at most the second or two spent
        // in the foreground on either side of the backgrounding.
        Assert.True(before - after <= 3,
            $"Countdown drained from {before}s to {after}s while backgrounded; it should pause.");
    }

    // Tapping the image advances to the next pose early (manual "done" gesture). The app
    // must stay alive and keep showing an image.
    [Fact]
    public void TappingImage_AdvancesToNextPose_WithoutCrashing()
    {
        if (Ready() is not { } g) return;

        // A long pose so the tap, not an expiry, is what advances the session.
        StartSession(g, imageCount: 3, seconds: 60);
        Assert.NotNull(g.WaitForId("session_image", TimeSpan.FromSeconds(10)));

        // Re-find rather than reusing the handle from the wait: the player repaints continuously, so
        // an element resolved even a moment earlier can already be stale by the time it is clicked.
        g.FindById("session_image").Click();

        Assert.Equal(UiTestEnvironment.AppPackage, g.Driver.CurrentPackage);
        Assert.True(g.WaitForId("session_image", TimeSpan.FromSeconds(5))?.Displayed == true,
            "An image should still be shown after advancing.");
    }

    // Galaxy Z Fold7 regression. Once the screen is wide enough the rail stops being a full-width
    // strip under the pose and becomes a fixed-width column beside it (rail_width), with the four
    // viewing-tool chips sharing one row inside that column. Under the old Chip style each chip
    // asked for its natural width, got less, and wrapped its label mid-word - "Grayscal / e". A
    // wrapped label is a two-line chip, so what this checks is that every tool chip is still exactly
    // as tall as a chip whose label cannot wrap, and that none of them spill outside the rail.
    [Fact]
    public void ToolChips_OnAWideScreen_StayOnOneLineInsideTheRail()
    {
        if (Ready() is not { } g) return;

        // A long pose: the assertions read the rail's geometry, so nothing should advance under them.
        StartSession(g, imageCount: 3, seconds: 60);
        Assert.NotNull(g.WaitForId("session_image", TimeSpan.FromSeconds(10)));

        var startingOrientation = g.Driver.Orientation;
        try
        {
            Assert.True(WidenUntilRailIsAColumn(g),
                "Could not get the player into its wide (fold-open) layout, where the rail is a column beside the pose.");

            // Bounds are taken against the window, not against session_rail: the rail is a plain
            // container, and the accessibility node reported for it covers only the region its
            // content occupies rather than the 328dp column the layout gives it.
            var screenWidth = g.Driver.Manage().Window.Size.Width;

            // "−" and "+" are one glyph each and cannot wrap at any width, so their height is what
            // a single-line chip measures on this device.
            var oneLine = g.FindById("chip_zoom_out").Size.Height;
            var chipIds = new[] { "chip_grayscale", "chip_flip", "chip_grid", "chip_blur" };
            var tops = new List<int>();

            foreach (var id in chipIds)
            {
                var chip = g.FindById(id);
                tops.Add(chip.Location.Y);

                Assert.True(Math.Abs(chip.Size.Height - oneLine) <= 1,
                    $"{id} is {chip.Size.Height}px tall against {oneLine}px for a single-line chip — its label wrapped.");
                Assert.True(chip.Location.X >= 0 && chip.Location.X + chip.Size.Width <= screenWidth,
                    $"{id} spans {chip.Location.X}..{chip.Location.X + chip.Size.Width}px, off a {screenWidth}px screen.");
            }

            // All four on one row: a chip pushed onto its own line is the other way the row can fail.
            Assert.True(tops.Max() - tops.Min() <= 1,
                $"The tool chips are not on one row (tops: {string.Join(", ", tops)}).");

            // The label that wrapped is the long one, and what stops it wrapping is being given a
            // wider share of the row than "Blur" gets. Equal shares are the broken state, and with
            // one line pinned they would clip rather than grow, so widths are what has to be
            // checked, not heights.
            var grayscale = g.FindById("chip_grayscale").Size.Width;
            var blur = g.FindById("chip_blur").Size.Width;
            Assert.True(grayscale >= blur * 1.4,
                $"chip_grayscale is {grayscale}px against {blur}px for chip_blur — 'Grayscale' needs a wider share of the row than 'Blur'.");
        }
        finally
        {
            g.Driver.Orientation = startingOrientation;
            Thread.Sleep(1000);
        }
    }

    // True once the player is in its wide layout. A foldable emulator is already wide unfolded, so
    // the rotation is only for running this on a phone-shaped device. session_progress_group is the
    // app's own tell: SessionActivity shows it only when the rail is a column (a Gone view drops out
    // of the hierarchy entirely, so "present" is the same question as "wide").
    static bool WidenUntilRailIsAColumn(AppiumGuard g)
    {
        if (IsWide(g)) return true;

        g.Driver.Orientation = ScreenOrientation.Landscape;
        return g.WaitUntil(_ => IsWide(g), TimeSpan.FromSeconds(10));
    }

    static bool IsWide(AppiumGuard g) =>
        g.FindAllById("session_progress_group").Any(v => v.Displayed);

    // "m:ss" / "h:mm:ss" -> total seconds.
    static int Seconds(string display)
    {
        var parts = display.Split(':').Select(int.Parse).ToArray();
        return parts.Length == 3
            ? parts[0] * 3600 + parts[1] * 60 + parts[2]
            : parts[0] * 60 + parts[1];
    }

    // Seed a folder with images, select it, set the pace, and tap Start. The break is pinned to
    // "None" because these tests time the pose itself — a rest between poses would show the break
    // overlay's clock instead and make the timings mean something else.
    static void StartSession(AppiumGuard g, int imageCount, int? seconds = null)
    {
        UiTestEnvironment.SeedDefaultFolder(imageCount);
        g.SelectDefaultFolder(expectImages: imageCount);
        g.OpenTab("tab_session");

        g.FindById("chip_break_0").Click();

        if (seconds is int s)
        {
            var input = g.FindById("seconds_input");
            input.Clear();
            input.SendKeys(s.ToString());
        }

        Assert.True(g.FindById("start_button").Enabled, "Start should be enabled after selecting images.");
        g.FindById("start_button").Click();
    }
}
