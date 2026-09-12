namespace DongoDraw.UITests;

// One emulator can host exactly one UiAutomator2 session: the driver installs a single
// io.appium.uiautomator2.server instrumentation on the device and force-stops any running instance
// when a session starts. xUnit parallelises test *classes* by default, so three classes each holding
// their own IClassFixture opened three sessions at once and each one killed the others' server —
// every test then failed with "The instrumentation process cannot be initialized", which reads like
// the app crashed rather than a harness conflict.
//
// Putting every UI-test class in one collection fixes both halves of that: a collection runs its
// classes sequentially, and a collection fixture is constructed once, so the whole suite shares a
// single driver session (and a single app install) instead of paying for one per class.
//
// A new UI-test class MUST carry [Collection(AppiumCollection.Name)]. The assembly-level
// DisableTestParallelization in AssemblyInfo.cs is the backstop for forgetting.
[CollectionDefinition(Name)]
public sealed class AppiumCollection : ICollectionFixture<AppiumAppFixture>
{
    public const string Name = "appium";
}
