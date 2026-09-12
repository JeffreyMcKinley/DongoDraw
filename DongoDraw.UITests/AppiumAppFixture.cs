using OpenQA.Selenium.Appium;
using OpenQA.Selenium.Appium.Android;

namespace DongoDraw.UITests;

// Boots one AndroidDriver session shared by a test class. When the suite is disabled
// (RUN_APPIUM != 1) it stays inert — Driver is null and no server connection is attempted — so
// the tests skip cleanly without an emulator. Reused across a class via IClassFixture.
public sealed class AppiumAppFixture : IDisposable
{
    public AndroidDriver? Driver { get; }

    // Non-null when construction failed while the suite was enabled, so tests surface a clear
    // reason (server down, APK missing) instead of a null-ref.
    public string? StartupError { get; }

    public AppiumAppFixture()
    {
        if (!UiTestEnvironment.Enabled)
            return;

        try
        {
            if (!File.Exists(UiTestEnvironment.SignedApkPath))
                throw new FileNotFoundException(
                    $"Signed APK not found at {UiTestEnvironment.SignedApkPath}. Build the app first.");

            var options = new AppiumOptions
            {
                PlatformName = "Android",
                AutomationName = "UiAutomator2",
                App = UiTestEnvironment.SignedApkPath,
            };
            // Give the app a moment to settle and don't reinstall if already present.
            options.AddAdditionalAppiumOption("appWaitActivity", "*");
            options.AddAdditionalAppiumOption("autoGrantPermissions", true);
            options.AddAdditionalAppiumOption("newCommandTimeout", 120);

            // The player screen repaints the countdown every 200ms, so its window is never "idle".
            // UiAutomator2 waits for idle before each lookup (10s by default), which on that screen
            // means every find stalls for the full timeout and then reports the snapshot taken when
            // the wait began — a countdown that appears frozen, and tests that take minutes. Nothing
            // here needs the idle wait: every screen is correct to read at any instant.
            options.AddAdditionalAppiumOption("settings[waitForIdleTimeout]", 0);

            Driver = new AndroidDriver(UiTestEnvironment.ServerUri, options, TimeSpan.FromMinutes(3));
            Driver.Manage().Timeouts().ImplicitWait = TimeSpan.FromSeconds(10);
        }
        catch (Exception ex)
        {
            StartupError = ex.Message;
        }
    }

    public void Dispose() => Driver?.Quit();
}
