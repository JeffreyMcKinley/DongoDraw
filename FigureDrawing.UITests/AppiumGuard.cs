using OpenQA.Selenium;
using OpenQA.Selenium.Appium;
using OpenQA.Selenium.Appium.Android;

namespace FigureDrawing.UITests;

// Thin helpers over the driver, keyed by the app's local resource ids. Shared by every UI-test
// class: they run against one device in one session (see AppiumCollection), so each test inherits
// whatever screen the previous one left behind and they all need the same way back to a known state.
internal sealed class AppiumGuard(AndroidDriver driver)
{
    public AndroidDriver Driver { get; } = driver;

    public IWebElement FindById(string localId) =>
        Driver.FindElement(MobileBy.Id(UiTestEnvironment.ViewId(localId)));

    // Elements matching an id, or none. Used to assert a view is *absent* (a View.Gone view drops
    // out of the hierarchy entirely).
    public IReadOnlyCollection<IWebElement> FindAllById(string localId) =>
        Driver.FindElements(MobileBy.Id(UiTestEnvironment.ViewId(localId)));

    // Returns the element once present, or null if it never appears within the timeout.
    public IWebElement? WaitForId(string localId, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                return FindById(localId);
            }
            catch (WebDriverException)
            {
                Thread.Sleep(250);
            }
        }

        return null;
    }

    // Session / Images / Settings are panes of one Activity since the Claude Design import, so a
    // test needing a control from another tab has to switch to it first.
    public void OpenTab(string tabId)
    {
        ReturnToMainScreen();
        FindById(tabId).Click();
        Thread.Sleep(400);
    }

    // Gets back to MainActivity, which is what owns the tab bar. Two different things can be in the
    // way and neither is handled by pressing Back blindly:
    //
    //   * the system folder picker — a different package, left open by a picker test;
    //   * the player screen — OUR package but a different Activity, with no tab bar at all, left
    //     open by any session test.
    //
    // Waiting for the package to match is not enough for the second case (it already matches), which
    // is why this keys off the Activity and re-activates the app after each Back.
    public void ReturnToMainScreen()
    {
        Driver.ActivateApp(UiTestEnvironment.AppPackage);

        for (var i = 0; i < 6 && !IsOnMainActivity; i++)
        {
            Driver.Navigate().Back();
            Thread.Sleep(700);
            Driver.ActivateApp(UiTestEnvironment.AppPackage);
        }
    }

    // Presses Back until the app is foregrounded again (e.g. to dismiss an open system picker left
    // by a prior test). Use ReturnToMainScreen when a MainActivity view is what's wanted.
    public void ReturnToApp()
    {
        for (var i = 0; i < 5 && Driver.CurrentPackage != UiTestEnvironment.AppPackage; i++)
        {
            Driver.Navigate().Back();
            Thread.Sleep(700);
        }
    }

    bool IsOnMainActivity =>
        (Driver.CurrentActivity ?? string.Empty).Contains("MainActivity", StringComparison.Ordinal);

    public bool WaitUntil(Func<AndroidDriver, bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                if (condition(Driver))
                    return true;
            }
            catch (WebDriverException)
            {
                // transient during an app/picker transition; retry until the deadline
            }

            Thread.Sleep(250);
        }

        return false;
    }

    // Runs a lookup with the implicit wait switched off. FindElements honours the session's 10s
    // implicit wait, so an "is it there right now?" probe would otherwise burn the whole budget of
    // whatever loop it sits in — and a WaitUntil around it would never get a second poll.
    T Probe<T>(Func<T> lookup)
    {
        var timeouts = Driver.Manage().Timeouts();
        var restore = timeouts.ImplicitWait;
        timeouts.ImplicitWait = TimeSpan.Zero;

        try
        {
            return lookup();
        }
        finally
        {
            timeouts.ImplicitWait = restore;
        }
    }

    // A row in the picker's file list, as opposed to the same text in the toolbar. DocumentsUI puts
    // the CURRENT folder's name in its toolbar, so a bare text() selector matches the breadcrumb of
    // the folder we are already inside — clicking that opens an ancestor drop-down over the confirm
    // button instead of navigating anywhere.
    static By PickerRow(string text) =>
        MobileBy.XPath($"//*[@text='{text}' and not(ancestor::*[contains(@resource-id,'toolbar')])]");

    // The same text in the toolbar: the picker's answer to "which folder am I in".
    static By PickerTitle(string text) =>
        MobileBy.XPath($"//*[contains(@resource-id,'toolbar')]//*[@text='{text}']");

    // Whether the picker is currently INSIDE the named folder. This is the positive check the
    // walk-into step needs: "the row is missing" alone is equally consistent with the picker sitting
    // somewhere else entirely, and confirming from there grants the wrong folder.
    public bool PickerIsInside(string folderName, TimeSpan timeout) =>
        WaitUntil(d => Probe(() => d.FindElements(PickerTitle(folderName)).Count > 0), timeout);

    // Whether the picker is listing a row with this exact text — a file or a subfolder, never the
    // toolbar. Used to assert what the picker is showing.
    public bool PickerShowsRow(string text, TimeSpan timeout) =>
        WaitUntil(d => Probe(() => d.FindElements(PickerRow(text)).Count > 0), timeout);

    // Taps a folder row in the picker, scrolling to it if the list is long. Returns false when no
    // such row is listed — which is the ordinary outcome once the app starts the picker inside that
    // folder, and is never on its own taken as proof of where the picker is.
    public bool TapPickerFolder(string name)
    {
        if (PickerShowsRow(name, TimeSpan.FromSeconds(5)))
        {
            Driver.FindElement(PickerRow(name)).Click();
            return true;
        }

        try
        {
            Driver.FindElement(MobileBy.AndroidUIAutomator(
                "new UiScrollable(new UiSelector().scrollable(true))" +
                $".scrollIntoView(new UiSelector().text(\"{name}\"))"));
        }
        catch (WebDriverException e)
        {
            Console.WriteLine($"no picker row '{name}': {e.Message.Split('\n')[0]}");
            return false;
        }

        if (!PickerShowsRow(name, TimeSpan.FromSeconds(2)))
            return false;

        Driver.FindElement(PickerRow(name)).Click();
        return true;
    }

    // Completes a real folder selection: open the picker from the Images tab, get inside the seeded
    // folder, "USE THIS FOLDER", then the Allow-access confirm dialog (both confirms are
    // android:id/button1 at different steps), then land back in the app. Shared because all three
    // suites need a granted library.
    //
    // Getting inside is not optional, and where the picker starts is no longer fixed. On a first
    // pick it opens wherever DocumentsUI left off — the root of shared storage on a cleared picker,
    // which Android refuses to grant ("Can't use this folder", no confirm button) — so the seeded
    // folder has to be walked into. Once a folder has been remembered, MainActivity supplies
    // EXTRA_INITIAL_URI and the picker is inside it already. Both are fine; being somewhere else is
    // not, and fails here rather than silently granting it.
    //
    // expectImages : when given, the number of images the folder was seeded with, asserted against
    //                the library once the app is back. This is the only check that the folder that
    //                was granted is the folder the test seeded.
    public void SelectDefaultFolder(int? expectImages = null)
    {
        var folder = UiTestEnvironment.DefaultPickerFolderName;

        OpenTab("tab_images");
        FindById("pick_button").Click();

        Assert.True(
            WaitUntil(d => d.CurrentPackage != UiTestEnvironment.AppPackage, TimeSpan.FromSeconds(15)),
            "The system folder picker never opened.");

        if (!PickerIsInside(folder, TimeSpan.FromSeconds(3)))
        {
            Assert.True(TapPickerFolder(folder),
                $"The picker is neither inside '{folder}' nor showing it as a row — " +
                "confirming from here would grant the wrong folder.");

            Assert.True(PickerIsInside(folder, TimeSpan.FromSeconds(10)),
                $"Tapped '{folder}' but the picker did not open it.");
        }

        ClickWhenPresent("android:id/button1", TimeSpan.FromSeconds(10));   // USE THIS FOLDER
        ClickWhenPresent("android:id/button1", TimeSpan.FromSeconds(5));    // Allow access? -> ALLOW

        Assert.True(
            WaitUntil(d => d.CurrentPackage == UiTestEnvironment.AppPackage, TimeSpan.FromSeconds(15)),
            "Never returned from the picker — the folder was not granted.");

        Assert.NotNull(WaitForId("pick_button", TimeSpan.FromSeconds(10)));

        // Wait for the load to reach a terminal state whether or not a count was asked for. The walk
        // is asynchronous since FD-009, so a caller that goes straight on to assert Start is enabled
        // would otherwise race the load — Start is gated on the pool being non-empty.
        //
        // Keyed on the loading caption clearing, not on the count: the pool is deliberately retained
        // across a re-walk of the same folder, so a count assertion is already satisfied before the
        // new walk lands. The caption is present for exactly the duration of a load.
        Assert.True(WaitForLoadToSettle(TimeSpan.FromSeconds(15)), "The picked folder never finished loading.");

        if (expectImages is not { } expected)
            return;

        if (expected == 0)
        {
            // The label's *text*, not merely its presence: since FD-009 the same view carries the
            // loading caption while the folder is being read, so waiting for the view alone would
            // pass the instant the walk started and stop asserting anything about the outcome.
            Assert.True(
                WaitForEmptyFolderMessage(TimeSpan.FromSeconds(15)),
                "The empty folder never reported that it holds no images.");
            return;
        }

        Assert.True(
            WaitUntil(_ => LibraryCount() == expected, TimeSpan.FromSeconds(10)),
            $"Granted folder holds {LibraryCount()} images, seeded {expected} — wrong folder granted.");
    }

    // Clicks a fully-qualified id once it appears, waiting up to the timeout. The picker's confirm
    // steps differ by Android version, so a step that never appears is not a failure here — the
    // caller asserts the outcome instead.
    void ClickWhenPresent(string fullId, TimeSpan timeout)
    {
        var by = MobileBy.Id(fullId);

        if (!WaitUntil(d => Probe(() => d.FindElements(by).Count > 0), timeout))
        {
            Console.WriteLine($"no '{fullId}' within {timeout.TotalSeconds}s");
            return;
        }

        try { Driver.FindElement(by).Click(); }
        catch (WebDriverException e) { Console.WriteLine($"'{fullId}' vanished: {e.Message.Split('\n')[0]}"); }
    }

    // Waits for the library pane to settle on "this folder holds no images", as opposed to the
    // loading caption the same label shows while the walk is running (FD-009). The expected wording
    // is owned by UiTestEnvironment so a copy edit in strings.xml has one place to be reflected.
    public bool WaitForEmptyFolderMessage(TimeSpan timeout) =>
        WaitUntil(_ => EmptyLabelText().Contains(
            UiTestEnvironment.EmptyFolderMessage, StringComparison.OrdinalIgnoreCase), timeout);

    // The empty-state label's current text, or "" when it is not on screen (it is View.Gone once a
    // folder has loaded images, so it drops out of the hierarchy).
    public string EmptyLabelText()
    {
        var labels = Probe(() => Driver.FindElements(MobileBy.Id(UiTestEnvironment.ViewId("empty_label"))));
        return labels.Count == 0 ? string.Empty : labels[0].Text ?? string.Empty;
    }

    // Waits for the library pane to report exactly this many images. The load is asynchronous since
    // FD-009, so every assertion about the pool has to be a wait rather than a point read.
    public bool WaitForLibraryCount(int expected, TimeSpan timeout) =>
        WaitUntil(_ => LibraryCount() == expected, timeout);

    // Waits for a load to finish, in whichever way it finishes: the loading caption is on screen for
    // exactly as long as the walk and the decodes are running, so its absence is the one signal that
    // means "settled" for a folder with images, an empty folder, and a folder that failed alike.
    public bool WaitForLoadToSettle(TimeSpan timeout)
    {
        // Two-sided. "Not loading" is also true *before* a load starts — the label is absent, or
        // still carries the first-run prompt — so waiting only for the caption to clear can return
        // before the walk has even begun. Wait for it to appear first; if it never does, the load
        // was already over by the time we looked, which is equally settled.
        WaitUntil(_ => IsLoading(), TimeSpan.FromSeconds(2));

        return WaitUntil(_ => !IsLoading(), timeout);
    }

    bool IsLoading() =>
        EmptyLabelText().Contains(UiTestEnvironment.LoadingMessage, StringComparison.OrdinalIgnoreCase);

    // Waits until the named Activity is in front. Used to prove a test actually navigated before it
    // navigates back — asserting the destination is what makes the return trip meaningful.
    public bool WaitForActivity(string activityName, TimeSpan timeout) =>
        WaitUntil(d => (d.CurrentActivity ?? string.Empty)
            .Contains(activityName, StringComparison.Ordinal), timeout);

    // How many preview tiles the reference grid is actually showing. Distinct from LibraryCount,
    // which reads the pool's size: since FD-009 the pool can be populated while the grid is empty
    // (released on stop, rebuilt on start), and telling those apart is the point.
    //
    // Counted structurally — ImageViews inside image_container — rather than by content description.
    // The description comes from a string resource, so keying off it would make this assertion fail
    // for a reason that has nothing to do with the grid.
    public int ThumbnailCount() =>
        Probe(() => Driver.FindElements(MobileBy.XPath(
            $"//*[@resource-id='{UiTestEnvironment.ViewId("image_container")}']" +
            "/android.widget.ImageView")).Count);

    // Deliberately "any", not a count: the grid scrolls, and UiAutomator reports only what is on
    // screen, so an exact count is a function of the device profile rather than of the app.
    public bool WaitForAnyThumbnail(TimeSpan timeout) =>
        WaitUntil(_ => ThumbnailCount() > 0, timeout);

    // The number the library pane is showing ("{0} images ready"), or -1 when it shows nothing yet.
    public int LibraryCount()
    {
        var labels = Probe(() => Driver.FindElements(MobileBy.Id(UiTestEnvironment.ViewId("library_count"))));
        if (labels.Count == 0)
            return -1;

        var digits = new string(labels[0].Text.TakeWhile(char.IsDigit).ToArray());
        return digits.Length > 0 ? int.Parse(digits) : -1;
    }
}
