using FigureDrawing.Core;

namespace FigureDrawing.Tests;

// The remembered-library rules (docs/DOMAIN-MODEL.md §5.1). These are the cases a stored value
// actually arrives in — blank, foreign, truncated, or fine — which is what the Android layer used
// to decide inline where nothing but a text-matching contract test could reach it.
public class LibraryReferenceTests
{
    const string TreeUri = "content://com.android.externalstorage.documents/tree/primary%3APictures";

    [Fact]
    public void AFreshInstall_HasNothingToRestore()
    {
        Assert.False(LibraryReference.TryParse(null, out var reference));
        Assert.Equal(string.Empty, reference);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t\n")]
    public void ABlankValue_IsNotAReference(string stored) =>
        Assert.False(LibraryReference.TryParse(stored, out _));

    // Garbage in the settings document is expected rather than exceptional: the file survives app
    // upgrades, restores from another device, and kills mid-write (INV-SET-P6).
    [Theory]
    [InlineData("not a uri")]
    [InlineData("/sdcard/Pictures")]
    [InlineData("file:///sdcard/Pictures")]
    [InlineData("http://example.com/tree/x")]
    public void AValueThatIsNotAContentUri_IsNotAReference(string stored) =>
        Assert.False(LibraryReference.TryParse(stored, out _));

    // The single-document form the picker returns for ACTION_OPEN_DOCUMENT, not the tree form
    // ACTION_OPEN_DOCUMENT_TREE returns. Asking the platform for a tree document id from one of
    // these throws, which is the failure this rule exists to stop.
    [Fact]
    public void ASingleDocumentUri_IsNotAReference() =>
        Assert.False(LibraryReference.TryParse(
            "content://com.android.externalstorage.documents/document/primary%3APictures", out _));

    // "content://.../tree/" with nothing after it carries no folder identity.
    [Fact]
    public void ATreeUriWithNoDocumentId_IsNotAReference() =>
        Assert.False(LibraryReference.TryParse(
            "content://com.android.externalstorage.documents/tree/", out _));

    [Fact]
    public void ATreeUri_IsAReference()
    {
        Assert.True(LibraryReference.TryParse(TreeUri, out var reference));
        Assert.Equal(TreeUri, reference);
    }

    // The scheme is case-insensitive per RFC 3986, and the value comes back canonical: lower-case
    // scheme, upper-case percent-escapes. The two sides of a grant comparison are strings from
    // different platform round-trips, so they have to be reduced to one spelling before they can be
    // compared at all.
    [Fact]
    public void TheSchemeIsCanonicalised_AndTheDocumentIdIsNot()
    {
        Assert.True(LibraryReference.TryParse(
            "  CONTENT://com.android.externalstorage.documents/tree/primary%3aPictures  ",
            out var reference));

        Assert.Equal(
            "content://com.android.externalstorage.documents/tree/primary%3APictures",
            reference);
    }

    // Case in the document id is meaningful — it is an opaque provider string (INV-IMG-1) — so only
    // the escapes are folded, never the characters they sit between.
    [Fact]
    public void TheDocumentIdKeepsItsCase()
    {
        Assert.True(LibraryReference.TryParse(
            "content://com.android.externalstorage.documents/tree/primary%3AMyPhotos", out var reference));

        Assert.Contains("MyPhotos", reference, StringComparison.Ordinal);
    }

    // A reference is one authority and one document id. A value long enough to threaten the Binder
    // transaction it is handed to has stopped being one (INV-REF-1).
    [Fact]
    public void AnAbsurdlyLongValue_IsNotAReference()
    {
        var tooLong = TreeUri + new string('x', LibraryReference.MaxLength);

        Assert.False(LibraryReference.TryParse(tooLong, out _));
    }

    // The tree segment has to be a path segment under a real authority. "tree" as the authority is
    // not one, and neither is a segment that merely starts with the same letters.
    [Theory]
    [InlineData("content://tree/x")]
    [InlineData("content:///tree/x")]
    [InlineData("content://com.example.documents/treex/y")]
    [InlineData("content://com.example.documents/tree")]
    public void AValueThatOnlyLooksLikeATree_IsNotAReference(string stored) =>
        Assert.False(LibraryReference.TryParse(stored, out _));

    [Fact]
    public void WithNoGrantsAtAll_NothingIsHeld() =>
        Assert.False(LibraryReference.HasReadGrant(TreeUri, []));

    [Fact]
    public void AGrantForAnotherFolder_IsNotThisOne() =>
        Assert.False(LibraryReference.HasReadGrant(TreeUri, [
            new PersistedGrant("content://com.android.externalstorage.documents/tree/primary%3ADownload", true),
        ]));

    // The app only ever takes the read flag, so a write-only entry belongs to something else.
    [Fact]
    public void AWriteOnlyGrant_DoesNotRestoreTheLibrary() =>
        Assert.False(LibraryReference.HasReadGrant(TreeUri, [new PersistedGrant(TreeUri, false)]));

    [Fact]
    public void AReadGrantForThisFolder_IsHeld() =>
        Assert.True(LibraryReference.HasReadGrant(TreeUri, [
            new PersistedGrant("content://com.android.externalstorage.documents/tree/primary%3ADownload", true),
            new PersistedGrant(TreeUri, true),
        ]));

    // A revoked grant leaves the reference behind, which is exactly the state a launch has to
    // survive (INV-GRP-5).
    [Fact]
    public void AReferenceWithoutItsGrant_ParsesButIsNotHeld()
    {
        Assert.True(LibraryReference.TryParse(TreeUri, out var reference));
        Assert.False(LibraryReference.HasReadGrant(reference, [new PersistedGrant(null, true)]));
    }

    // The stored reference and the platform's grant list are two different round-trips through the
    // same folder, and they do not always come back spelled identically. Every one of these is the
    // folder the artist picked, and reading any of them as "some other folder" strands the app in
    // the remembered-but-unavailable state with no way back except re-picking (INV-REF-3).
    [Theory]
    [InlineData("content://com.android.externalstorage.documents/tree/primary%3aPics")]
    [InlineData("CONTENT://com.android.externalstorage.documents/tree/primary%3APics")]
    [InlineData("  content://com.android.externalstorage.documents/tree/primary%3APics  ")]
    public void AGrantSpelledDifferently_IsStillThisFolder(string asReportedByThePlatform)
    {
        const string stored = "content://com.android.externalstorage.documents/tree/primary%3APics";

        Assert.True(LibraryReference.HasReadGrant(stored, [new PersistedGrant(asReportedByThePlatform, true)]));
    }

    // Canonicalising must not make two different folders look like one.
    [Fact]
    public void ADifferentDocumentId_IsADifferentFolder() =>
        Assert.False(LibraryReference.HasReadGrant(
            "content://com.android.externalstorage.documents/tree/primary%3APics",
            [new PersistedGrant("content://com.android.externalstorage.documents/tree/primary%3Apics", true)]));

    [Fact]
    public void RePickingTheSameFolder_ReleasesNothing() =>
        Assert.Empty(LibraryReference.GrantsToRelease(TreeUri, [new PersistedGrant(TreeUri, true)]));

    // The platform caps a package's persisted grants and drops the oldest past the cap, so a grant
    // for a folder the app has moved on from is a grant that can cost it the one it still uses.
    [Fact]
    public void PickingADifferentFolder_ReleasesTheOldGrant()
    {
        const string previous = "content://com.android.externalstorage.documents/tree/primary%3ADownload";

        var stale = LibraryReference.GrantsToRelease(TreeUri, [
            new PersistedGrant(previous, true),
            new PersistedGrant(TreeUri, true),
        ]);

        Assert.Equal([previous], stale);
    }

    // Released by the platform's own spelling, not ours: that string is what identifies the grant to
    // release, and canonicalising it could hand back something the platform does not recognise.
    [Fact]
    public void AStaleGrant_IsReleasedAsThePlatformSpellsIt()
    {
        const string asReported = "content://com.android.externalstorage.documents/tree/primary%3adownload";

        Assert.Equal([asReported], LibraryReference.GrantsToRelease(TreeUri, [new PersistedGrant(asReported, true)]));
    }

    // Write-only entries are not this app's to release: it never takes a write grant.
    [Fact]
    public void AWriteOnlyGrant_IsLeftAlone() =>
        Assert.Empty(LibraryReference.GrantsToRelease(TreeUri, [new PersistedGrant("content://other/tree/x", false)]));

    [Fact]
    public void WithNothingToKeep_EveryReadGrantIsStale() =>
        Assert.Equal(
            [TreeUri],
            LibraryReference.GrantsToRelease(null, [new PersistedGrant(TreeUri, true)]));

    // The four things the library pane can be looking at. The screen maps these to messages; it does
    // not decide them (INV-REF-5).
    [Fact]
    public void WithNothingStored_TheLibraryWasNeverPicked() =>
        Assert.Equal(LibraryStatus.NeverPicked, LibraryReference.Classify(null, [], 0));

    [Fact]
    public void WithAnUnusableStoredValue_TheLibraryWasNeverPicked() =>
        Assert.Equal(LibraryStatus.NeverPicked, LibraryReference.Classify("not a uri", [], 0));

    [Fact]
    public void WithTheGrantGone_TheLibraryIsUnavailable() =>
        Assert.Equal(LibraryStatus.Unavailable, LibraryReference.Classify(TreeUri, [], 0));

    // A grant that is still listed can still be unusable — an unmounted volume, an uninstalled
    // provider — and a walk that threw is not an empty folder.
    [Fact]
    public void WithAWalkThatFailed_TheLibraryIsUnavailable() =>
        Assert.Equal(
            LibraryStatus.Unavailable,
            LibraryReference.Classify(TreeUri, [new PersistedGrant(TreeUri, true)], 0, walkFailed: true));

    [Fact]
    public void WithAReadableFolderAndNoImages_TheLibraryIsEmpty() =>
        Assert.Equal(
            LibraryStatus.Empty,
            LibraryReference.Classify(TreeUri, [new PersistedGrant(TreeUri, true)], 0));

    [Fact]
    public void WithImages_TheLibraryIsReady() =>
        Assert.Equal(
            LibraryStatus.Ready,
            LibraryReference.Classify(TreeUri, [new PersistedGrant(TreeUri, true)], 12));

    [Fact]
    public void AGrantIsNeverHeldForNothing() =>
        Assert.False(LibraryReference.HasReadGrant(null, [new PersistedGrant(null, true)]));
}
