using System.Text.RegularExpressions;

namespace FigureDrawing.Tests;

// Contract tests for "the app remembers the folder you picked" (docs/ARCHITECTURE.md §11).
//
// The rules behind the feature are Core's and are unit tested in LibraryReferenceTests. What is
// left is wiring across a boundary Core cannot see — a persistable URI grant, one persisted string,
// and the extra that tells the system picker where to open — so it is pinned here by reading
// MainActivity as a file, the same tier that already guards the resource lookups. The behaviour is
// covered on a device by the folder-memory tests in FolderPickerUiTests.
//
// Comments and string literals are stripped before anything is asserted: a tier whose assertions a
// comment could satisfy would stay green through the exact deletion it exists to catch. What is
// asserted is which API is reached from which method, never how a statement is spelled — a rename
// or a reformat must not fail a build that still behaves.
public class FolderMemoryContractTests
{
    static readonly SourceContract Activity = new("MainActivity.cs");

    static string Source => Activity.Text;

    // MainActivity with every comment, string and char literal blanked out, positions preserved.
    static string Code => Activity.Code;

    static string MethodBody(string methodName) => Activity.MethodBody(methodName);

    // The stripper itself is exercised against fixtures in SourceContractTests. What belongs here is
    // that the real MainActivity survives the same pass — a stripper that dropped a line would make
    // every assertion below vacuous.
    [Fact]
    public void MainActivity_SurvivesTheStripper()
    {
        Assert.Equal(Source.Length, Code.Length);
        Assert.DoesNotContain("figuredrawing.db", Code, StringComparison.Ordinal);
    }

    // How wide the handoff is is a session-setup rule with its own unit tests, but nothing else pins
    // that the screen actually asks for it: reverting this one call restores the flat bound that
    // INV-POOL-6 was narrowed away from, with a green suite.
    [Fact]
    public void StartingASession_BoundsTheHandoffByTheSessionsLength() =>
        Assert.Contains("SessionSetup.HandoffBound(", MethodBody("StartSession"), StringComparison.Ordinal);

    // Without a persisted grant the folder is remembered and unreadable: the uri survives, the
    // permission does not, and every relaunch shows the empty state.
    [Fact]
    public void PickingAFolder_TakesAPersistableReadGrant()
    {
        var body = MethodBody("OnActivityResult");

        Assert.Contains("TakePersistableUriPermission", body, StringComparison.Ordinal);
        Assert.Contains("GrantReadUriPermission", body, StringComparison.Ordinal);

        // Read-only against the artist's own storage: the app never writes into the library, so a
        // write flag on the grant would be privilege it has no use for.
        Assert.DoesNotContain("GrantWriteUriPermission", Code, StringComparison.Ordinal);
    }

    // The folder's identity is persisted, and persisted at the moment it is picked — Settings only
    // reaches disk on Save (docs/ARCHITECTURE.md §6), and the pick is one of the named write
    // moments (INV-SET-P4), so an assignment without one is forgotten on exit.
    [Fact]
    public void PickingAFolder_PersistsItAsLastCollection()
    {
        var body = MethodBody("OnActivityResult");

        var assignment = Regex.Match(body, @"settings\.LastCollection\s*=");
        Assert.True(assignment.Success, "The picked folder is no longer written to Settings.LastCollection.");
        Assert.Contains("settings.Save();", body[assignment.Index..], StringComparison.Ordinal);
    }

    // Only the root's identity is persisted, never its contents (INV-GRP-1): the pool is re-walked
    // on restore, so a folder edited between launches needs no migration.
    [Fact]
    public void NothingButTheFolderIdentity_IsPersisted()
    {
        var assigned = Regex.Matches(Code, @"settings\.LastCollection\s*=\s*(?<value>[^;]+);")
            .Select(m => m.Groups["value"].Value)
            .ToList();

        Assert.NotEmpty(assigned);
        Assert.All(assigned, value =>
        {
            Assert.DoesNotContain("Pool", value, StringComparison.Ordinal);
            Assert.DoesNotContain("library", value, StringComparison.Ordinal);
            Assert.DoesNotContain("Sample", value, StringComparison.Ordinal);
        });
    }

    // Restoring is OnCreate work: a launch has to come up with the library already loaded rather
    // than waiting for the artist to visit the Images tab.
    [Fact]
    public void Launching_RestoresTheRememberedFolder()
    {
        Assert.Contains("RestoreLastFolder();", MethodBody("OnCreate"), StringComparison.Ordinal);
    }

    // Whether a remembered folder is worth acting on is Core's rule, not the screen's — the Activity
    // may parse the uri and enumerate the platform's grants, and nothing else (§14).
    [Fact]
    public void WhetherTheRememberedFolderIsUsable_IsCoreRule()
    {
        Assert.Contains("LibraryReference.TryParse", MethodBody("RememberedTree"), StringComparison.Ordinal);
        Assert.Contains("LibraryReference.HasReadGrant", MethodBody("RestoreLastFolder"), StringComparison.Ordinal);
        Assert.Contains("PersistedUriPermissions", MethodBody("PersistedGrants"), StringComparison.Ordinal);
    }

    // A remembered folder whose grant has since been revoked (permission cleared, volume unmounted,
    // provider uninstalled) must leave the empty state showing, not throw on every launch from then
    // on — the stale uri is persisted, so an escape here reproduces forever (INV-GRP-5).
    [Fact]
    public void Restoring_ChecksTheGrantAndSurvivesAFailure()
    {
        var body = MethodBody("RestoreLastFolder");

        Assert.Contains("LibraryReference.TryParse", body, StringComparison.Ordinal);

        var caught = body.IndexOf("catch (Exception", StringComparison.Ordinal);
        Assert.True(caught >= 0, "RestoreLastFolder no longer catches a failed load.");
        Assert.Contains("ShowRememberedFolderUnavailable();", body[caught..], StringComparison.Ordinal);

        // Whatever the screen says, the library it is showing has to be emptied: a half-restored
        // pool would leave Start open on images the session cannot read.
        Assert.Contains("ResetLibrary();", MethodBody("ShowRememberedFolderUnavailable"), StringComparison.Ordinal);

        // The grant refresh is best effort and must never fail a restore that otherwise worked.
        Assert.Contains("catch (Exception", MethodBody("RefreshGrant"), StringComparison.Ordinal);
    }

    // The remembered folder is where the picker opens, so reopening the same library is one tap.
    [Fact]
    public void PickingAgain_StartsThePickerInTheRememberedFolder()
    {
        var body = MethodBody("PickFolder");

        Assert.Contains("Intent.ActionOpenDocumentTree", body, StringComparison.Ordinal);
        Assert.Contains("DocumentsContract.ExtraInitialUri", body, StringComparison.Ordinal);

        // Launching it crosses the system boundary: an image with no documents provider must show a
        // message, not take the process down on a tap (INV-X-11).
        Assert.Contains("catch (Exception", body, StringComparison.Ordinal);
    }

    // Handed the bare tree uri, DocumentsUI lands at the root of the provider instead of inside the
    // folder — the hint has to be the tree's *document* uri.
    [Fact]
    public void ThePickerHint_IsTheTreesDocumentUri()
    {
        var body = MethodBody("LastPickedDocumentUri");

        Assert.Contains("RememberedTree()", body, StringComparison.Ordinal);
        Assert.Contains("DocumentsContract.GetTreeDocumentId", body, StringComparison.Ordinal);
        Assert.Contains("DocumentsContract.BuildDocumentUriUsingTree", body, StringComparison.Ordinal);
    }

    // Settings are written at named moments (INV-SET-P4), and leaving the screen is one of them: a
    // process swiped off the recents list never runs OnDestroy, so a value still sitting only in
    // memory when the artist closes the app is a value they lose.
    [Fact]
    public void LeavingTheScreen_WritesSettingsToTheDatabase()
    {
        var body = MethodBody("OnPause");

        // The typed inputs live nowhere else, so without capturing them the write has nothing new to
        // persist and the backstop is decorative.
        Assert.Contains("CaptureTypedInputs();", body, StringComparison.Ordinal);
        Assert.Contains("SaveSettings();", body, StringComparison.Ordinal);

        // A failed write on the way out must not take the screen down with it — and must be visible,
        // because a preference set that silently stops persisting is this whole feature failing.
        var save = MethodBody("SaveSettings");
        Assert.Contains("catch (Exception", save, StringComparison.Ordinal);
        Assert.Contains("Log.Error", save, StringComparison.Ordinal);
        Assert.DoesNotContain("throw", save, StringComparison.Ordinal);
    }

    // A remembered folder that cannot be reopened is not the same state as never having picked one,
    // and must not read like it on screen: the artist needs to know their choice is still known and
    // what went wrong with it, or "it forgot my folder" is the only available reading.
    [Fact]
    public void ARememberedFolderThatCannotBeReopened_SaysSo()
    {
        var body = MethodBody("RenderLibrary");

        // Which of the three empty states the artist is in is Core's classification; the screen only
        // maps it to a resource, and each state maps to a different one.
        Assert.Contains("LibraryReference.Classify", body, StringComparison.Ordinal);
        Assert.Contains("LibraryStatus.Unavailable => Resource.String.folder_unavailable_text", body, StringComparison.Ordinal);
        Assert.Contains("LibraryStatus.Empty => Resource.String.empty_folder_text", body, StringComparison.Ordinal);
        Assert.Contains("Resource.String.empty_label_text", body, StringComparison.Ordinal);
    }

    // Restoring needs the grant; pointing the picker does not. Requiring one for the other is what
    // turns a revoked permission into a second problem — the artist re-picking the folder from the
    // provider root instead of from where they left off.
    [Fact]
    public void ThePickerHint_DoesNotRequireAGrant()
    {
        Assert.DoesNotContain("HasReadGrant", MethodBody("LastPickedDocumentUri"), StringComparison.Ordinal);
        Assert.DoesNotContain("HasReadGrant", MethodBody("RememberedTree"), StringComparison.Ordinal);
        Assert.Contains("LibraryReference.HasReadGrant", MethodBody("RestoreLastFolder"), StringComparison.Ordinal);
    }

    // A grant survives being taken again, and taking it again on a successful restore is what keeps
    // the folder in daily use from being the oldest entry when the platform trims the list.
    [Fact]
    public void RestoringAFolder_RefreshesItsGrant()
    {
        // Twice in the file: once where a folder is picked, once where a remembered one is restored.
        // Asserted on the API rather than on which private method happens to hold it.
        var taken = Regex.Matches(Code, @"TakePersistableUriPermission").Count;

        Assert.True(taken >= 2, $"Expected the grant taken on pick AND refreshed on restore; found {taken} call(s).");
    }

    // A grant kept for a folder the artist has moved on from can cost them the one they still use:
    // the platform caps a package's persisted grants and drops the oldest past the cap.
    [Fact]
    public void PickingADifferentFolder_ReleasesTheGrantItSupersedes()
    {
        Assert.Contains("LibraryReference.GrantsToRelease", Code, StringComparison.Ordinal);
        Assert.Contains("ReleasePersistableUriPermission", Code, StringComparison.Ordinal);
        Assert.Contains("ReleaseSupersededGrants", MethodBody("OnActivityResult"), StringComparison.Ordinal);
    }

    // A hint is a convenience; failing to build one must never cost the artist the picker itself.
    [Fact]
    public void AnUnusableHint_LeavesThePickerOpening()
    {
        var hint = MethodBody("LastPickedDocumentUri");

        var caught = hint.IndexOf("catch (Exception", StringComparison.Ordinal);
        Assert.True(caught >= 0, "LastPickedDocumentUri no longer catches a failed hint.");
        Assert.Contains("return null;", hint[caught..], StringComparison.Ordinal);

        // Guarded at the call site too, so an absent hint is an absent extra rather than a crash:
        // the hint is fetched and tested before the extra is ever attached.
        var body = MethodBody("PickFolder");
        var attached = body.IndexOf("PutExtra", StringComparison.Ordinal);
        Assert.True(attached >= 0, "PickFolder no longer attaches the picker hint.");

        // The conditional has to test the hint itself: "contains an if" would be satisfied by
        // `if (true) intent.PutExtra(..., LastPickedDocumentUri())`.
        Assert.Matches(@"if\s*\(\s*LastPickedDocumentUri\(\)\s*(is|!=)", body[..attached]);
    }
}
