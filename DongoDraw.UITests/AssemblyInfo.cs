// Backstop for the one-session-per-device rule described in AppiumCollection: even if a new test
// class forgets [Collection(AppiumCollection.Name)], nothing in this assembly may run in parallel
// with anything else. Two concurrent Appium sessions against one emulator kill each other's
// UiAutomator2 server.
[assembly: Xunit.CollectionBehavior(DisableTestParallelization = true)]
