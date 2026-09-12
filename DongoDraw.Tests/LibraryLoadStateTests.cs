using DongoDraw.Core;

namespace DongoDraw.Tests;

// When a re-walk may keep the pool the screen is showing (#4). Executable rather than pinned by
// source shape: inverted, Start stays armed over another folder's pool and every contract assertion
// about this comparison still passes.
public class LibraryLoadStateTests
{
    const string Folder = "content://com.android.externalstorage.documents/tree/primary%3APictures";

    // The case the retention exists for: returning from a session re-walks the same folder, and the
    // previous complete pool keeps Start armed while it runs.
    [Fact]
    public void ReLoadingTheSameFolder_KeepsThePool() =>
        Assert.True(LibraryLoadState.KeepsPool(Folder, Folder));

    [Fact]
    public void LoadingADifferentFolder_DropsThePool() =>
        Assert.False(LibraryLoadState.KeepsPool(Folder, Folder + "2"));

    // Nothing loaded yet, so there is nothing to keep — a first pick always clears to empty.
    [Fact]
    public void AFirstLoad_KeepsNothing() =>
        Assert.False(LibraryLoadState.KeepsPool(null, Folder));

    // A tree that cannot even be named is never the folder already on screen.
    [Fact]
    public void AnUnnameableTree_KeepsNothing() =>
        Assert.False(LibraryLoadState.KeepsPool(Folder, null));

    // The bug this comparison replaced: two providers can report the same document id, so identity
    // has to be the whole tree uri. Same folder name, same document id, different authority.
    [Fact]
    public void ASameNamedFolderOnAnotherProvider_DropsThePool()
    {
        const string sdCard = "content://com.example.sdprovider/tree/primary%3APictures";

        Assert.False(LibraryLoadState.KeepsPool(Folder, sdCard));
    }

    // Uri comparison is ordinal: content uris are case-sensitive in their path, and two folders
    // differing only in case are two folders.
    [Fact]
    public void FoldersDifferingOnlyInCase_AreDifferentFolders() =>
        Assert.False(LibraryLoadState.KeepsPool(Folder, Folder.ToUpperInvariant()));
}
