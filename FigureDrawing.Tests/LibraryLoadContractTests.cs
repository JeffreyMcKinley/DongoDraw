namespace FigureDrawing.Tests;

// Contract tests for FD-009: the reference library loads off the UI thread, and a load that has
// been superseded writes nothing (INV-X-13, "a library load is abandonable").
//
// A separate file from FolderMemoryContractTests because it is a separate invariant family
// (docs/DOMAIN-MODEL.md §8), and it reads two sources: LibraryLoader owns the walk, the decodes and
// the abandonment guard, while MainActivity owns the lifecycle that drives them.
//
// This tier is a regression fence for named decisions, not a proof. It cannot see a scheduler,
// cannot prove the walk left the UI thread, and cannot detect a leaked bitmap. Every assertion below
// is therefore a decision a reviewer might plausibly reverse — the orderings especially, which are
// the difference between "a superseded load is discarded" and "a superseded load installs a
// truncated pool with no error anywhere". Several pin private method names; that is the same
// practice this suite already uses for RestoreLastFolder, and updating them alongside a rename is
// correct rather than a smell.
public class LibraryLoadContractTests
{
    static readonly SourceContract Loader = new("LibraryLoader.cs");
    static readonly SourceContract Activity = new("MainActivity.cs");

    // --- The load runs off the UI thread ---------------------------------------

    // The whole point of the ticket: the SAF walk (one blocking provider query per folder) and the
    // preview decodes must not happen on the thread that draws.
    [Fact]
    public void TheWalkAndTheDecodes_RunOffTheUiThread()
    {
        var body = Loader.MethodBody("LoadAsync");

        // Tied to the walk, not merely present: an awaited Task.Run over something else would leave
        // the walk on the calling thread and still satisfy a bare "it awaits a Task.Run".
        Assert.Matches(@"await Task\.Run\(\(\)\s*=>\s*WalkAndDecode\(", body);
    }

    // The background worker is static so that "it cannot touch a view, the Activity, or Settings" is
    // a compiler guarantee rather than a review comment. An instance lambda would capture `this` and
    // pin a destroyed Activity's whole view tree for the length of the walk.
    [Fact]
    public void TheBackgroundWorker_IsStaticAndTouchesNoView()
    {
        Assert.Matches(@"(?m)^\s*static\b[^\n]*\bWalkAndDecode\s*\(", Loader.Code);

        // Over the whole loader, not just the worker's body: members of MainActivity are already
        // unreachable from another class, so asserting the worker does not name them tests the
        // compiler. What is genuinely reversible is someone handing the loader a view, an Activity
        // or Settings — that is what this fences, and it is why the loader takes the *application*
        // resolver: an Activity's reaches back to the Activity through its ContextImpl.
        foreach (var forbidden in new[]
                 { "ImageView", "Activity", "View ", "Settings", "FindViewById" })
            Assert.DoesNotContain(forbidden, Loader.Code, StringComparison.Ordinal);
    }

    // Blocking on the load would restore the freeze this ticket removes, and .Result / .Wait()
    // against the main-looper SynchronizationContext deadlocks outright.
    [Theory]
    [InlineData(".Wait()")]
    [InlineData(".Result")]
    [InlineData("GetAwaiter()")]
    public void TheLoad_IsNeverWaitedOnSynchronously(string blocking)
    {
        Assert.DoesNotContain(blocking, Loader.Code, StringComparison.Ordinal);
        Assert.DoesNotContain(blocking, Activity.Code, StringComparison.Ordinal);
    }

    // 24 concurrent full-size decodes is the out-of-memory case MaxThumbnailDimension's budget is
    // written against. One background pass, decoding one image at a time.
    [Fact]
    public void ThePreviews_AreDecodedSequentially()
    {
        Assert.DoesNotContain("Parallel.", Loader.Code, StringComparison.Ordinal);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(Loader.Code, @"Task\.Run\("));

        // A single Task.Run does not by itself make the decodes sequential — Task.WhenAll over the
        // pool inside it would be the same 24-concurrent-decode out-of-memory. The loop is the rule.
        Assert.DoesNotContain("WhenAll", Loader.Code, StringComparison.Ordinal);
        Assert.Matches(@"foreach\s*\([^)]*library\.Pool\.Take\(", Loader.MethodBody("WalkAndDecode"));
    }

    // The cap bounds decode ATTEMPTS, not successes, and the walk stops decoding the moment it is
    // abandoned — the decode is the expensive half, so a check only on the queries leaves an
    // abandoned load allocating full-size bitmaps against a folder nobody is waiting for.
    [Fact]
    public void TheDecodeLoop_IsBoundedAndStopsWhenAbandoned()
    {
        var body = Loader.MethodBody("WalkAndDecode");

        Assert.Matches(@"library\.Pool\.Take\(maxThumbnails\)", body);

        var loop = SourceContract.IndexOf(body, "foreach", "The decode loop is gone.");
        var guard = body.IndexOf("!isCurrent()", loop, StringComparison.Ordinal);
        var decode = body.IndexOf("DecodeThumbnail(", loop, StringComparison.Ordinal);

        // Polarity included: inverted, nothing is ever decoded and the grid is permanently blank.
        Assert.True(guard > loop, "The decode loop no longer checks whether its load is current.");
        Assert.True(guard < decode, "The abandonment check must precede each decode.");

        var abandoned = SourceContract.BlockAfter(body, guard + "!isCurrent()".Length);

        // Freed on the worker that decoded them, not left for the continuation, so an abandoned
        // load's previews do not stack on top of the load that superseded it.
        Assert.Contains("DiscardThumbnails(", abandoned, StringComparison.Ordinal);

        // And it actually stops: without the break an abandoned load decodes all 24 anyway and
        // simply frees them one at a time, which is the cost this test is named for.
        Assert.Contains("break;", abandoned, StringComparison.Ordinal);
    }

    // --- A superseded load writes nothing (INV-X-13) ---------------------------

    // Every load takes a number on the way out. Taking it FIRST is what makes a load that never
    // reaches the walk still supersede the one in flight; take it later and the previous load
    // survives to overwrite the folder the artist has since picked.
    [Fact]
    public void EveryLoad_TakesItsGenerationBeforeAnythingElse()
    {
        var body = Loader.MethodBody("LoadAsync");

        var take = SourceContract.IndexOf(body, "generation.Take()", "LoadAsync no longer takes a generation.");

        // Before the FIRST exit, not merely before the await: an early return that ran ahead of the
        // ticket would let a load that never reaches the walk fail to supersede the one in flight,
        // which is the whole reason the ticket is taken up front.
        var firstExit = SourceContract.IndexOf(body, "return", "LoadAsync no longer returns anything.");

        Assert.True(take < firstExit, "The generation must be taken before any other statement can exit.");
    }

    // The guard is read before anything is handed back. An abandoned walk still produces a
    // ReferenceLibrary — with a truncated pool — so a check that landed after the hand-back would
    // silently install a fraction of the artist's folder with no error anywhere.
    [Fact]
    public void ASupersededLoad_IsDiscardedBeforeItCanBeHandedBack()
    {
        var body = Loader.MethodBody("LoadAsync");

        // The re-check specifically, not the word "generation" — the first occurrence of that is the
        // ticket being *taken* at the top of the method, which would make this ordering true however
        // the guard were mangled.
        var guard = SourceContract.IndexOf(
            body, "!IsCurrent(mine)", "LoadAsync no longer re-checks its generation after the walk.");

        // The last hand-back, not the first: an early return for an unusable tree precedes the walk.
        var returned = body.LastIndexOf("return new LibraryLoad", StringComparison.Ordinal);
        Assert.True(guard < returned, "The generation must be re-checked before the load is handed back.");

        // What the guarded branch has to do: free the previews and report nothing.
        var discarded = body.IndexOf("DiscardThumbnails(", guard, StringComparison.Ordinal);
        var abandoned = body.IndexOf("return null;", guard, StringComparison.Ordinal);

        Assert.True(discarded > guard, "A superseded load no longer frees what it decoded.");
        Assert.True(abandoned > discarded, "A superseded load must report nothing after freeing its previews.");
        Assert.True(abandoned < returned, "The abandoned path must return before the completed one.");
    }

    // A load nobody is waiting for reports nothing, including its failures. Without this the failure
    // path bypasses the guard entirely: a stale load's exception wipes the live load's pool, captions
    // a folder that is fine as unopenable, and abandons the load that was about to succeed.
    [Fact]
    public void ASupersededLoad_DoesNotReportItsFailure()
    {
        var body = Loader.MethodBody("LoadAsync");

        var caught = SourceContract.IndexOf(
            body, "catch (Exception", "LoadAsync no longer catches a failed walk.");

        var guarded = body.IndexOf("!IsCurrent(mine)", caught, StringComparison.Ordinal);
        var swallowed = body.IndexOf("return null;", caught, StringComparison.Ordinal);
        var rethrown = body.IndexOf("throw;", caught, StringComparison.Ordinal);

        Assert.True(guarded > caught, "The failure path no longer checks whether its load is current.");

        // The return, not just the check: logging the abandoned failure and rethrowing anyway would
        // satisfy an ordering assertion while letting a stale load wipe the live one's pool.
        Assert.True(swallowed > guarded, "A superseded load's failure is no longer swallowed.");
        Assert.True(swallowed < rethrown, "A superseded load must return before the rethrow.");
    }

    // Bitmaps an abandoned load decoded are recycled, never left to a finalizer pass — the same rule
    // ClearThumbnails follows for the ones that made it onto the grid (docs/ARCHITECTURE.md §8).
    [Fact]
    public void AnAbandonedLoad_FreesWhatItDecoded()
    {
        var body = Loader.MethodBody("DiscardThumbnails");

        Assert.Contains(".Recycle();", body, StringComparison.Ordinal);
        Assert.Contains(".Dispose();", body, StringComparison.Ordinal);

        // Asserted from the guard onwards: a bare "DiscardThumbnails appears somewhere in LoadAsync"
        // is satisfied by the catch block alone, so the leak this criterion names — the abandoned
        // branch dropping its previews — would survive its own test.
        var load = Loader.MethodBody("LoadAsync");
        var guard = SourceContract.IndexOf(
            load, "!IsCurrent(mine)", "LoadAsync no longer re-checks its generation after the walk.");

        // Inside the abandoned branch, not merely after it: the catch block's own discard is also
        // textually later, so a positional assertion is satisfied by code on a different path.
        Assert.Contains(
            "DiscardThumbnails(",
            SourceContract.BlockAfter(load, guard + "!IsCurrent(mine)".Length),
            StringComparison.Ordinal);
    }

    // The walk stops asking once its load is abandoned. Returning nothing rather than throwing is
    // the answer this adapter already gives a revoked grant (INV-TREE-4), so no threading concept
    // reaches the port and Core is untouched.
    [Fact]
    public void AnAbandonedWalk_StopsQueryingTheProvider()
    {
        var body = Loader.MethodBody("GetChildren");

        // The polarity too: `if (isCurrent()) return entries;` reads the same to an ordering
        // assertion and would make every live walk report an empty folder.
        Assert.Matches(@"if\s*\(\s*!\s*isCurrent\(\)\s*\)", body);

        var guard = SourceContract.IndexOf(
            body, "isCurrent()", "The SAF adapter no longer checks whether its load is current.");
        var query = SourceContract.IndexOf(
            body, "resolver.Query", "The SAF adapter no longer queries the provider.");

        Assert.True(guard < query, "The abandonment check must come before the provider query.");
    }

    // The pool is replaced wholesale, never mutated in place. ReferenceLibrary.Enumerate() rewrites
    // Pool on the instance it is called on, so calling it on the live library from a background
    // thread would publish a half-built pool to a screen that is already reading it.
    [Fact]
    public void TheLiveLibrary_IsNeverReEnumeratedInPlace()
    {
        // Any receiver, not just the `library` field: loaded.Library.Enumerate() would rebuild the
        // pool in place on the instance the screen is already reading.
        Assert.DoesNotContain("Enumerate()", Activity.Code, StringComparison.Ordinal);
        Assert.DoesNotContain("Enumerate()", Loader.Code, StringComparison.Ordinal);
    }

    // The screen assigns the pool only after the guard has cleared it.
    [Fact]
    public void TheScreen_ChecksTheLoadLandedBeforeItRendersIt()
    {
        var body = Activity.MethodBody("LoadFolderAsync");

        var guard = SourceContract.IndexOf(
            body, "loaded is null", "LoadFolderAsync no longer tests whether the load was abandoned.");
        var assigned = SourceContract.IndexOf(
            body, "library =", "LoadFolderAsync no longer assigns the loaded library.");

        Assert.True(guard < assigned, "The abandoned load must be discarded before the pool is assigned.");
    }

    // Everything after the await runs on the looper with no caller left to catch it: the try/catch
    // in RestoreLastFolder and OnActivityResult only ever covered the synchronous prologue. On the
    // restore path, which runs off a persisted uri, an escape is a crash on every launch (INV-X-11).
    [Fact]
    public void TheLoadsTail_CatchesItsOwnFailures()
    {
        var body = Activity.MethodBody("LoadFolderAsync");

        // And reports it: an empty catch would satisfy "catches its own failures" while losing the
        // folder-error message, leaving the pane captioned "Reading that folder…" forever.
        var caught = SourceContract.IndexOf(
            body, "catch (Exception", "LoadFolderAsync no longer catches its own failures.");

        Assert.Contains("ShowRememberedFolderUnavailable();", body[caught..], StringComparison.Ordinal);
    }

    // The abandonment rule is Core's, and executed by LoadGenerationTests. This pins that the loader
    // actually uses it: an inlined counter here would behave identically today while leaving those
    // tests covering code that no longer ships.
    [Fact]
    public void TheLoader_UsesTheCoreAbandonmentRule()
    {
        Assert.Matches(@"LoadGeneration\s+generation", Loader.Code);
        Assert.DoesNotContain("Interlocked", Loader.Code, StringComparison.Ordinal);
        Assert.DoesNotContain("Volatile", Loader.Code, StringComparison.Ordinal);
    }

    // Likewise for the pool-retention comparison: inlined back into the Activity it would be
    // unreachable by any test that can execute it.
    [Fact]
    public void TheScreen_UsesTheCoreRetentionRule()
    {
        Assert.Contains("LibraryLoadState.KeepsPool(", Activity.MethodBody("LoadFolder"), StringComparison.Ordinal);
    }

    // The *application* resolver. An Activity's reaches back to the Activity through its ContextImpl,
    // so a walk still running after OnDestroy would pin the destroyed screen's whole view tree —
    // the leak the loader is written to avoid, reintroduced at its construction site.
    [Fact]
    public void TheLoader_IsGivenTheApplicationResolver()
    {
        Assert.Matches(@"new LibraryLoader\(\s*ApplicationContext", Activity.Code);
    }

    // A load that lands after OnStop released the grid must not repopulate it. Structural rather
    // than resting on the awaiter inlining its continuation in the same looper turn.
    [Fact]
    public void ALoadLandingAfterTheGridWasReleased_IsDiscarded()
    {
        var body = Activity.MethodBody("LoadFolderAsync");

        var guard = System.Text.RegularExpressions.Regex.Match(body, @"if\s*\(\s*gridReleased\s*\)");
        Assert.True(guard.Success, "LoadFolderAsync no longer checks whether the grid was released.");

        var branch = SourceContract.BlockAfter(body, guard.Index + guard.Length);
        Assert.Contains("DiscardThumbnails(", branch, StringComparison.Ordinal);
        Assert.Contains("return;", branch, StringComparison.Ordinal);

        Assert.True(
            guard.Index < SourceContract.IndexOf(body, "AttachThumbnails(", "The previews are no longer attached."),
            "The released-grid check must come before the previews are attached.");
    }

    // The folder-error caption lands after the reset, or RenderLibrary's "No images found in that
    // folder" overwrites it and an unopenable folder reads as an empty one.
    [Fact]
    public void TheFolderError_IsCaptionedAfterTheReset()
    {
        // The caption itself is chosen in Core (LibraryReference.Classify): the screen only raises
        // walkFailed, so a folder that could not be read reads as Unavailable rather than as one
        // that turned out to be empty. What has to hold here is that the flag is set *before* the
        // render that consults it, and lowered afterwards so it does not stick to the next load.
        var body = Activity.MethodBody("ShowRememberedFolderUnavailable");

        var raised = SourceContract.IndexOf(body, "walkFailed = true;", "The failure is no longer recorded.");
        var rendered = SourceContract.IndexOf(body, "ResetLibrary();", "The failure no longer re-renders.");
        var lowered = SourceContract.IndexOf(body, "walkFailed = false;", "The failure flag is never lowered.");

        Assert.True(raised < rendered, "The failure must be recorded before the state is re-rendered.");
        Assert.True(rendered < lowered, "The flag must outlive the render that reads it.");
    }

    // --- Lifecycle -------------------------------------------------------------

    // A screen that is going away abandons its load. OnStop as well as OnDestroy: MainActivity is
    // stopped, not destroyed, while a session runs, and a load landing after the grid was released
    // would put back the previews that release exists to remove.
    [Theory]
    [InlineData("OnStop")]
    [InlineData("OnDestroy")]
    public void AScreenThatGoesAway_AbandonsItsLoad(string method)
    {
        // OnDestroy null-conditionals it: OnCreate can throw before the loader field is assigned,
        // the same reason settings?.Dispose() is guarded there.
        Assert.Matches(@"loader\??\.Abandon\(\);", Activity.MethodBody(method));
    }

    // Abandon before releasing, or a load already past its guard repopulates the grid that was just
    // cleared.
    [Fact]
    public void OnStop_AbandonsBeforeItReleasesTheGrid()
    {
        var body = Activity.MethodBody("OnStop");

        var abandoned = SourceContract.IndexOf(body, "Abandon();", "OnStop no longer abandons.");
        var cleared = SourceContract.IndexOf(body, "ClearThumbnails();", "OnStop no longer releases the grid.");

        Assert.True(abandoned < cleared, "OnStop must abandon the load before releasing the grid.");
    }

    // Not OnPause. A briefly backgrounded app — a notification shade, a permission dialog — must not
    // come back to an empty library, which is why the release is tied to Stopped rather than Paused.
    [Fact]
    public void ABrieflyBackgroundedApp_DoesNotAbandonItsLoad()
    {
        // Stated as "these three methods and nowhere else" rather than "OnPause does not abandon".
        // MainActivity declares no OnPause today, so the negative form asserted nothing at all — and
        // this version stays falsifiable when someone adds one.
        // Spans, not substring searches: two methods can share a body, and one body can be a prefix
        // of another, either of which would send a substring lookup to the wrong place.
        var allowed = new[] { "OnStop", "OnDestroy", "ResetLibrary" }
            .Select(Activity.MethodBodySpan)
            .ToList();

        var calls = System.Text.RegularExpressions.Regex.Matches(Activity.Code, @"Abandon\(");

        // Non-empty, not a count: how many call sites exist is unrelated to how many methods may
        // hold one, and a second defensive abandon inside an allowed method breaks no rule.
        Assert.NotEmpty(calls);

        foreach (System.Text.RegularExpressions.Match call in calls)
        {
            var line = Activity.Code[..call.Index].Count(c => c == '\n') + 1;
            Assert.True(
                allowed.Any(span => span.Start <= call.Index && call.Index < span.Start + span.Length),
                $"MainActivity.cs:{line} abandons the load outside OnStop/OnDestroy/ResetLibrary. " +
                "A briefly backgrounded app must come back to its library, not an empty one.");
        }
    }

    // The grid is released when the screen stops and rebuilt when it starts, so a session's decodes
    // are not stacked on top of the library's 24 previews.
    [Fact]
    public void TheGrid_IsReleasedOnStopAndRepopulatedOnStart()
    {
        Assert.Contains("ClearThumbnails();", Activity.MethodBody("OnStop"), StringComparison.Ordinal);
        Assert.Contains("RestoreLastFolder(", Activity.MethodBody("OnStart"), StringComparison.Ordinal);
    }

    // OnStart fires immediately after OnCreate, which has already restored the remembered folder, so
    // the repopulate has to be conditional or every cold start walks the tree twice.
    [Fact]
    public void AColdStart_WalksTheFolderOnlyOnce()
    {
        var body = Activity.MethodBody("OnStart");

        // The polarity, not just the presence: inverted, every cold start walks twice AND no return
        // from a session ever repopulates — this ticket's stated top risk — and an assertion that
        // only checked ordering would stay green through it.
        Assert.Matches(@"if\s*\(\s*!\s*gridReleased\s*\)\s*return;", body);
        Assert.Contains("gridReleased = false;", body, StringComparison.Ordinal);

        var guard = SourceContract.IndexOf(body, "gridReleased", "OnStart no longer guards the repopulate.");
        var reload = SourceContract.IndexOf(body, "RestoreLastFolder(", "OnStart no longer repopulates.");

        Assert.True(guard < reload, "OnStart must test whether the grid was released before reloading.");
    }

    // Resetting to the empty state cancels whatever is in flight. Without this a load already
    // running when the folder error lands would quietly repopulate the folder the artist was just
    // told could not be opened — and its RenderLibrary would clobber the error message.
    [Fact]
    public void ResettingTheLibrary_AbandonsAnyLoadInFlight()
    {
        var body = Activity.MethodBody("ResetLibrary");

        var abandoned = SourceContract.IndexOf(body, "Abandon();", "ResetLibrary no longer abandons.");
        var cleared = SourceContract.IndexOf(body, "ClearThumbnails();", "ResetLibrary no longer clears.");

        Assert.True(abandoned < cleared, "ResetLibrary must abandon before it clears.");
    }

    // The folder is persisted the moment it is picked, before the walk starts — OnStart reconstructs
    // the target from Settings.LastCollection, so a pick that gets backgrounded mid-walk must still
    // be remembered. An await here would also make OnActivityResult a second async void.
    [Fact]
    public void HandlingAPickedFolder_StaysSynchronous()
    {
        Assert.DoesNotContain("await ", Activity.MethodBody("OnActivityResult"), StringComparison.Ordinal);
    }

    // Every decoded preview is either owned by a view or freed — never both, never neither. The
    // release must follow the attach, or a tile the grid is showing gets recycled out from under it;
    // the finally is what frees the tail when a tile fails to attach.
    [Fact]
    public void TheGrid_TakesOwnershipOfEveryPreviewItAttaches()
    {
        var body = Activity.MethodBody("AttachThumbnails");

        var added = SourceContract.IndexOf(body, "AddThumbnail(", "The previews are no longer attached.");

        // The slot is cleared only after the view has taken the bitmap. Matched by shape rather than
        // by the exact spelling of the assignment, which is an implementation choice.
        var release = System.Text.RegularExpressions.Regex.Match(
            body[added..], @"thumbnails\[\s*i\s*\]\s*=\s*null\s*!?\s*;");
        Assert.True(release.Success, "An attached preview is no longer released to the view.");

        // A tile that will not attach costs that tile and nothing else.
        Assert.Contains("catch (Exception", body[added..], StringComparison.Ordinal);

        var final = SourceContract.IndexOf(body, "finally", "AttachThumbnails no longer frees the undelivered tail.");
        Assert.Contains("DiscardThumbnails(", body[final..], StringComparison.Ordinal);

        // The discard has to tolerate the cleared slots, or a successful load throws out of its own
        // finally and a perfectly good folder is captioned as unopenable.
        Assert.Contains("is null", Loader.MethodBody("DiscardThumbnails"), StringComparison.Ordinal);
    }

    // Settings is disposed in OnDestroy, and the load's tail reads it through Draft(). Abandoning
    // first is what guarantees no continuation runs against a torn-down screen.
    [Fact]
    public void OnDestroy_AbandonsBeforeItDisposesWhatTheLoadReads()
    {
        var body = Activity.MethodBody("OnDestroy");

        var abandoned = SourceContract.IndexOf(body, "Abandon();", "OnDestroy no longer abandons.");
        var disposed = SourceContract.IndexOf(body, "settings?.Dispose();", "OnDestroy no longer disposes Settings.");

        Assert.True(abandoned < disposed, "The load must be abandoned before Settings is disposed.");
    }

    // The pool is kept across a re-walk of the SAME folder only, and identity is the whole tree uri:
    // a document id like "primary:Pictures" is not unique across providers, so comparing ids would
    // keep an SD card's pool armed under a cloud folder's name. Inverted or widened, Start is armed
    // over a pool that belongs to a different folder — the "never a mixture" clause of INV-X-13.
    [Fact]
    public void OnlyARewalkOfTheSameFolder_KeepsThePreviousPool()
    {
        var body = Activity.MethodBody("LoadFolder");

        // Delegated to Core, where LibraryLoadStateTests can actually execute it — comparing here
        // would be a decision no source-shape assertion can tell from its own inverse.
        var compared = System.Text.RegularExpressions.Regex.Match(
            body, @"if\s*\(\s*!\s*LibraryLoadState\.KeepsPool\(\s*loadedTreeUri\s*,\s*treeUri\.ToString\(\)\s*\)\s*\)");
        Assert.True(compared.Success, "LoadFolder no longer asks Core whether the pool survives.");

        // INSIDE the branch, not merely after it: hoisted out of the `if`, every walk would clear
        // the pool and Start would grey out on every return from a session.
        var branch = SourceContract.BlockAfter(body, compared.Index + compared.Length);

        Assert.Contains("library = ReferenceLibrary.Empty;", branch, StringComparison.Ordinal);
        Assert.Contains("loadedTreeUri = treeUri.ToString();", branch, StringComparison.Ordinal);

        // Dropping the pool without dropping the folder it belonged to would let the next pick of
        // that folder "keep" a pool that no longer exists.
        Assert.Contains("loadedTreeUri = null;", Activity.MethodBody("ResetLibrary"), StringComparison.Ordinal);
    }

    // A grant revoked while the screen was stopped must not leave the released grid under a header
    // still reporting the old folder's count, with Start armed over images that no longer resolve.
    [Fact]
    public void AFolderLostWhileStopped_IsNoticedOnTheWayBack()
    {
        // The handling itself is master's and is tested in FolderMemoryContractTests: a reference
        // that no longer parses keeps the first-run prompt, and a grant that has gone shows the
        // remembered-but-unreachable state. What FD-009 adds is a second caller — OnStart now
        // rebuilds a grid OnStop released, so a folder that went away while the screen was stopped
        // has to reach that same handling instead of leaving a stale count over a dead pool.
        Assert.Contains("RestoreLastFolder(", Activity.MethodBody("OnStart"), StringComparison.Ordinal);

        var restore = Activity.MethodBody("RestoreLastFolder");
        Assert.Contains("ShowRememberedFolderUnavailable();", restore, StringComparison.Ordinal);
    }

    // The picker stops this screen and its result arrives *after* OnStart, while LastCollection
    // still names the old folder. Without the flag, every pick walks the folder being replaced.
    [Fact]
    public void APickInFlight_DefersTheRebuildToItsResult()
    {
        var pick = Activity.MethodBody("PickFolder");
        Assert.True(
            SourceContract.IndexOf(pick, "awaitingPickResult = true;", "PickFolder no longer records the pick.")
                < SourceContract.IndexOf(pick, "StartActivityForResult(", "PickFolder no longer opens the picker."),
            "The pick must be recorded before the picker can stop this screen.");

        var start = Activity.MethodBody("OnStart");
        var deferred = System.Text.RegularExpressions.Regex.Match(start, @"if\s*\(\s*awaitingPickResult\s*\)");
        Assert.True(deferred.Success, "OnStart no longer defers to the pick result.");

        Assert.Contains(
            "return",
            SourceContract.BlockAfter(start, deferred.Index + deferred.Length),
            StringComparison.Ordinal);

        // And before the reload, or the deferral does nothing.
        Assert.True(
            deferred.Index < SourceContract.IndexOf(start, "RestoreLastFolder(", "OnStart no longer reloads."),
            "The deferral must come before the reload it is deferring.");

        // The released-grid flag is cleared before that return, or a successful pick's previews are
        // discarded by LoadFolderAsync's released-grid guard and the grid comes back blank.
        Assert.True(
            SourceContract.IndexOf(start, "gridReleased = false;", "OnStart no longer clears the released flag.")
                < deferred.Index,
            "gridReleased must be cleared before OnStart defers to the pick result.");
    }

    // Every exit from the pick clears the flag, or the screen stops rebuilding itself for good.
    [Fact]
    public void EveryPickOutcome_ClearsTheFlagAndLeavesAGrid()
    {
        // The picker failing to open is an exit too — a device with no document picker.
        Assert.Contains("awaitingPickResult = false;", Activity.MethodBody("PickFolder"), StringComparison.Ordinal);

        var result = Activity.MethodBody("OnActivityResult");
        var cleared = SourceContract.IndexOf(
            result, "awaitingPickResult = false;", "OnActivityResult no longer clears the pick.");

        // Cancelled, or returned without a folder: OnStop released the grid and OnStart deferred to
        // this method, so returning without rebuilding leaves the artist looking at an empty grid.
        var noFolder = System.Text.RegularExpressions.Regex.Match(result, @"if\s*\(\s*treeUri is null\s*\)");
        Assert.True(noFolder.Success, "OnActivityResult no longer handles a pick that produced no folder.");
        Assert.True(cleared < noFolder.Index, "The pick must be cleared before its result is inspected.");

        Assert.Contains(
            "RestoreLastFolder();",
            SourceContract.BlockAfter(result, noFolder.Index + noFolder.Length),
            StringComparison.Ordinal);
    }

    // --- The loading state -----------------------------------------------------

    // A folder being read is not a folder that turned out to be empty. RenderLibrary writes
    // empty_folder_text for an empty library, so the loading caption has to be a separate path.
    [Fact]
    public void AFolderBeingRead_IsNotCaptionedAsEmpty()
    {
        var body = Activity.MethodBody("ShowLoading");

        Assert.Contains("Resource.String.library_loading_text", body, StringComparison.Ordinal);
        Assert.DoesNotContain("RenderLibrary", body, StringComparison.Ordinal);

        // And it is actually shown, after RenderLibrary rather than before: RenderLibrary captions an
        // empty library "No images found" and reveals "+N more not shown" against a cleared grid, so
        // the two lines swapped would caption a folder being read as one with nothing in it.
        var load = Activity.MethodBody("LoadFolder");
        var rendered = SourceContract.IndexOf(load, "RenderLibrary();", "LoadFolder no longer renders.");
        var loading = SourceContract.IndexOf(load, "ShowLoading();", "LoadFolder no longer shows the loading state.");

        Assert.True(rendered < loading, "The loading caption must land after RenderLibrary or it is clobbered.");

        // "+N more not shown" counts the pool against the grid, and during a re-walk the grid is
        // empty while the previous pool is retained — which would read as every image being hidden.
        Assert.Contains("libraryMore.Visibility", body, StringComparison.Ordinal);
    }
}
