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
    // MainActivity as text, comments and literals blanked. The machinery lives in SourceShape,
    // which FD-009 shared out so LibraryLoadContractTests can read LibraryLoader.cs the same way.
    static readonly SourceShape Activity = new("MainActivity.cs");

    static string Source => Activity.Source;
    static string Code => Activity.Code;
    static string MethodBody(string methodName) => Activity.MethodBody(methodName);

    // The stripper is what makes every assertion below mean something, so it is checked rather than
    // trusted: MainActivity's own comments name most of the APIs asserted for.
    [Fact]
    public void TheStripper_RemovesCommentsAndLiterals()
    {
        Assert.Equal(Source.Length, Code.Length);
        Assert.DoesNotContain("Storage Access Framework", Code, StringComparison.Ordinal);
        Assert.DoesNotContain("figuredrawing.db", Code, StringComparison.Ordinal);
        Assert.Contains("DocumentsContract.ExtraInitialUri", Code, StringComparison.Ordinal);
    }

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
        var body = MethodBody("RememberedTree");

        Assert.Contains("LibraryReference.TryParse", body, StringComparison.Ordinal);
        Assert.Contains("LibraryReference.HasReadGrant", body, StringComparison.Ordinal);
        Assert.Contains("PersistedUriPermissions", MethodBody("PersistedGrants"), StringComparison.Ordinal);

        // Enumerating the platform's grants is a binder call, and since FD-009 this runs on every
        // return to the screen rather than once per launch — so an escape here would be a crash on
        // every foreground, off a reference that is persisted (INV-X-11, INV-GRP-5).
        var caught = body.IndexOf("catch (Exception", StringComparison.Ordinal);
        Assert.True(caught >= 0, "Checking the remembered grant no longer survives a platform failure.");
        Assert.Contains("return null;", body[caught..], StringComparison.Ordinal);
    }

    // A remembered folder whose grant has since been revoked (permission cleared, volume unmounted,
    // provider uninstalled) must leave the empty state showing, not throw on every launch from then
    // on — the stale uri is persisted, so an escape here reproduces forever (INV-GRP-5).
    //
    // Since FD-009 this catch covers only LoadFolder's synchronous prologue: the walk itself fails
    // on the looper, inside LoadFolderAsync. The asynchronous half of this invariant is pinned by
    // LibraryLoadContractTests.TheLoadsTail_CatchesItsOwnFailures.
    [Fact]
    public void Restoring_ChecksTheGrantAndSurvivesAFailure()
    {
        var body = MethodBody("RestoreLastFolder");

        Assert.Contains("RememberedTree()", body, StringComparison.Ordinal);

        var caught = body.IndexOf("catch (Exception", StringComparison.Ordinal);
        Assert.True(caught >= 0, "RestoreLastFolder no longer catches a failed load.");
        Assert.Contains("ResetLibrary();", body[caught..], StringComparison.Ordinal);
    }

    // The remembered folder is where the picker opens, so reopening the same library is one tap.
    [Fact]
    public void PickingAgain_StartsThePickerInTheRememberedFolder()
    {
        var body = MethodBody("PickFolder");

        Assert.Contains("Intent.ActionOpenDocumentTree", body, StringComparison.Ordinal);
        Assert.Contains("DocumentsContract.ExtraInitialUri", body, StringComparison.Ordinal);
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

        var before = body[..attached];
        Assert.Contains("LastPickedDocumentUri()", before, StringComparison.Ordinal);
        Assert.Matches(@"\bif\b", before);
    }
}
