using Xunit.Abstractions;

namespace FigureDrawing.UITests;

// FD-002 session-setup end-to-end tests over Appium/UiAutomator2. Opt-in via RUN_APPIUM=1 (see
// UiTestEnvironment) — otherwise every test skips-as-pass. Exercises the setup inputs and the Start
// gate on a real emulator; the pure validation is covered by SessionSetupTests.
//
// Setup lives on the Session tab and the folder picker on the Images tab, so these tests switch
// between the two (AppiumGuard.OpenTab).
[Collection(AppiumCollection.Name)]
public class SessionSetupUiTests(AppiumAppFixture app, ITestOutputHelper output)
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

    [Fact]
    public void SetupScreen_ShowsInputsAndStartButton()
    {
        if (Ready() is not { } g) return;
        g.OpenTab("tab_session");

        Assert.True(g.FindById("seconds_input").Displayed);
        Assert.True(g.FindById("count_input").Displayed);
        Assert.True(g.FindById("start_button").Displayed);
    }

    [Fact]
    public void Inputs_SeededFromSettings_WithPositiveDefaults()
    {
        if (Ready() is not { } g) return;
        g.OpenTab("tab_session");

        Assert.True(int.TryParse(g.FindById("seconds_input").Text, out var seconds) && seconds > 0,
            $"seconds_input not seeded with a positive value: '{g.FindById("seconds_input").Text}'");
        Assert.True(int.TryParse(g.FindById("count_input").Text, out var count) && count > 0,
            $"count_input not seeded with a positive value: '{g.FindById("count_input").Text}'");
    }

    // Start is gated on a folder being selected. Selecting a folder that contains images, with the
    // (valid) default inputs, must enable Start.
    [Fact]
    public void SelectingFolderWithImages_EnablesStart()
    {
        if (Ready() is not { } g) return;

        UiTestEnvironment.SeedDefaultFolder(imageCount: 3);
        g.SelectDefaultFolder(expectImages: 3);
        g.OpenTab("tab_session");

        Assert.True(g.FindById("start_button").Enabled,
            "Start should be enabled once a folder with images is selected and inputs are valid.");
    }

    // With a folder selected, clearing an input (invalid) disables Start; restoring it re-enables.
    [Fact]
    public void InvalidInput_DisablesStart()
    {
        if (Ready() is not { } g) return;

        UiTestEnvironment.SeedDefaultFolder(imageCount: 3);
        g.SelectDefaultFolder(expectImages: 3);
        g.OpenTab("tab_session");
        Assert.True(g.FindById("start_button").Enabled);

        var seconds = g.FindById("seconds_input");
        seconds.Clear();
        Assert.False(g.FindById("start_button").Enabled,
            "Start should be disabled while seconds-per-image is empty.");

        seconds.SendKeys("30");
        Assert.True(g.FindById("start_button").Enabled,
            "Start should re-enable once a valid seconds value is entered.");
    }
}
