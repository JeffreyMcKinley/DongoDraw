using FigureDrawing.Core;

namespace FigureDrawing.Tests;

// The reference library: what counts as a drawable image, the recursive walk under a picked folder,
// and the pool it hands to a session (INV-IMG-3, INV-GRP-*, INV-POOL-*). The Android UI/SAF
// plumbing is not exercised here; the library is the pure core behind it.
public class ReferenceLibraryTests
{
    // In-memory IDocumentTree: maps a folder document ID to its direct children.
    sealed class FakeTree(Dictionary<string, DocumentEntry[]> children) : IDocumentTree
    {
        public int Walks { get; private set; }

        public IEnumerable<DocumentEntry> GetChildren(string parentDocumentId)
        {
            Walks++;
            return children.TryGetValue(parentDocumentId, out var kids) ? kids : [];
        }

        public void Replace(string parentDocumentId, params DocumentEntry[] kids) =>
            children[parentDocumentId] = kids;
    }

    const string Dir = ReferenceLibrary.DirectoryMimeType;

    static DocumentEntry Img(string id, string mime = "image/jpeg") => new(id, mime);
    static DocumentEntry Folder(string id) => new(id, Dir);
    static DocumentEntry File(string id, string mime) => new(id, mime);

    static IReadOnlyList<string> Pool(FakeTree tree, string root = "root") =>
        new ReferenceLibrary(tree, root).Pool;

    [Theory]
    [InlineData("image/jpeg", true)]
    [InlineData("image/png", true)]
    [InlineData("image/webp", true)]
    [InlineData("image/heic", true)]
    [InlineData("image/svg+xml", true)]
    [InlineData("application/pdf", false)]
    [InlineData("text/plain", false)]
    [InlineData(Dir, false)]
    [InlineData(null, false)]
    public void IsImage_ClassifiesByMimePrefix(string? mime, bool expected) =>
        Assert.Equal(expected, ReferenceLibrary.IsImage(mime));

    [Fact]
    public void Pool_ReturnsTopLevelImages()
    {
        var tree = new FakeTree(new()
        {
            ["root"] = [Img("a"), Img("b"), Img("c")],
        });

        Assert.Equal(new[] { "a", "b", "c" }, Pool(tree));
    }

    [Fact]
    public void Pool_ExcludesNonImageFiles()
    {
        var tree = new FakeTree(new()
        {
            ["root"] = [Img("pic"), File("notes", "text/plain"), File("doc", "application/pdf")],
        });

        Assert.Equal(new[] { "pic" }, Pool(tree));
    }

    [Fact]
    public void Pool_RecursesIntoSubfolders_InEncounterOrder()
    {
        var tree = new FakeTree(new()
        {
            ["root"] = [Img("top"), Folder("sub")],
            ["sub"] = [Img("nested"), Folder("deep")],
            ["deep"] = [Img("deepest")],
        });

        Assert.Equal(new[] { "top", "nested", "deepest" }, Pool(tree));
    }

    [Fact]
    public void EmptyFolder_IsEmpty_NotAnError()
    {
        var tree = new FakeTree(new() { ["root"] = [] });
        var library = new ReferenceLibrary(tree, "root");

        Assert.Empty(library.Pool);
        Assert.True(library.IsEmpty);
        Assert.Equal(0, library.Count);
    }

    [Fact]
    public void FolderWithOnlyNonImages_IsEmpty()
    {
        var tree = new FakeTree(new()
        {
            ["root"] = [File("a", "text/plain"), Folder("sub")],
            ["sub"] = [File("b", "application/zip")],
        });

        Assert.Empty(Pool(tree));
    }

    [Fact]
    public void SkipsEmptySubfolderWithoutCrashing()
    {
        var tree = new FakeTree(new()
        {
            ["root"] = [Folder("empty"), Img("keep")],
            // "empty" intentionally has no entry in the map.
        });

        Assert.Equal(new[] { "keep" }, Pool(tree));
    }

    [Fact]
    public void CyclicTree_DoesNotLoopForever()
    {
        // Malformed provider: "a" reports "b" as a child folder and "b" reports "a" back.
        var tree = new FakeTree(new()
        {
            ["root"] = [Folder("a")],
            ["a"] = [Img("x"), Folder("b")],
            ["b"] = [Img("y"), Folder("a")],
        });

        Assert.Equal(new[] { "x", "y" }, Pool(tree));
    }

    // The same image reached through two subfolders is one entry in the pool, not two — repetition
    // within a session comes from passes, never from a pool that lists an image twice (INV-POOL-2).
    [Fact]
    public void Pool_HasNoDuplicates_WhenAnImageIsReachedTwice()
    {
        var tree = new FakeTree(new()
        {
            ["root"] = [Folder("one"), Folder("two")],
            ["one"] = [Img("shared"), Img("only-one")],
            ["two"] = [Img("shared"), Img("only-two")],
        });

        Assert.Equal(new[] { "shared", "only-one", "only-two" }, Pool(tree));
    }

    // The visited set stops a reported cycle; a provider that invents a fresh id at every level
    // never repeats one, so depth is what stops it. Unbounded recursion here is a stack overflow,
    // which kills the process rather than raising something catchable (INV-GRP-3).
    [Fact]
    public void UnboundedlyDeepTree_StopsInsteadOfOverflowing()
    {
        // "d0" contains "d1" contains "d2"… forever, each level also holding one image.
        var tree = new EndlessTree();

        var library = new ReferenceLibrary(tree, "d0");

        Assert.NotEmpty(library.Pool);
        Assert.True(library.Count < 200, $"walked {library.Count} levels — the depth cap did not hold");
    }

    sealed class EndlessTree : IDocumentTree
    {
        public IEnumerable<DocumentEntry> GetChildren(string parentDocumentId)
        {
            var level = int.Parse(parentDocumentId[1..]);
            yield return new DocumentEntry($"image-{level}", "image/jpeg");
            yield return new DocumentEntry($"d{level + 1}", Dir);
        }
    }

    // A mapper backed by the Android adapter can fail on a document id the provider reports from
    // outside the tree. One bad row drops that entry; it never aborts the walk (INV-X-11).
    [Fact]
    public void ToImageId_ThatThrows_DropsTheEntryAndKeepsWalking()
    {
        var tree = new FakeTree(new() { ["root"] = [Img("good"), Img("bad"), Img("also-good")] });

        var library = new ReferenceLibrary(
            tree, "root",
            toImageId: id => id == "bad" ? throw new InvalidOperationException("outside the tree") : id);

        Assert.Equal(new[] { "good", "also-good" }, library.Pool);
    }

    [Fact]
    public void NullArguments_Throw()
    {
        var tree = new FakeTree(new());

        Assert.Throws<ArgumentNullException>(() => new ReferenceLibrary(null!, "root"));
        Assert.Throws<ArgumentNullException>(() => new ReferenceLibrary(tree, null!));
    }

    [Fact]
    public void Library_KeepsItsRootIdentityAndName()
    {
        var tree = new FakeTree(new() { ["root"] = [Img("a")] });
        var library = new ReferenceLibrary(tree, "root", "Pics");

        Assert.Equal("root", library.RootDocumentId);
        Assert.Equal("Pics", library.DisplayName);
        Assert.Equal(1, library.Count);
        Assert.False(library.IsEmpty);
    }

    // The Android layer maps each document id to the durable content URI a session draws from, so
    // the pool needs no second pass at the call site.
    [Fact]
    public void ToImageId_MapsEachDocumentIdIntoThePool()
    {
        var tree = new FakeTree(new() { ["root"] = [Img("a"), Img("b")] });

        var library = new ReferenceLibrary(tree, "root", toImageId: id => $"content://tree/{id}");

        Assert.Equal(new[] { "content://tree/a", "content://tree/b" }, library.Pool);
    }

    [Fact]
    public void ToImageId_ReturningNull_DropsTheEntry()
    {
        var tree = new FakeTree(new() { ["root"] = [Img("keep"), Img("drop")] });

        var library = new ReferenceLibrary(tree, "root", toImageId: id => id == "drop" ? null : id);

        Assert.Equal(new[] { "keep" }, library.Pool);
    }

    // Membership is derived, never stored (INV-GRP-1): re-enumerating picks up an edited folder.
    [Fact]
    public void ReWalking_PicksUpAnEditedFolder()
    {
        var tree = new FakeTree(new() { ["root"] = [Img("a")] });
        var library = new ReferenceLibrary(tree, "root");
        Assert.Equal(new[] { "a" }, library.Pool);

        // A fresh instance, which is how production re-derives: the walk is the constructor, and
        // rebuilding a live library's pool in place would publish it half-built (INV-X-13).
        tree.Replace("root", Img("a"), Img("b"));
        var reloaded = new ReferenceLibrary(tree, "root");

        Assert.Equal(new[] { "a", "b" }, reloaded.Pool);
    }

    // A walk that is abandoned part-way stops being answered by the tree (#4 pairs the SAF
    // adapter with an abandonment check, so a superseded load's queries return nothing). The domain
    // side is an ordinary partial pool in encounter order, with no throw: membership is
    // whatever the tree reports now (INV-GRP-1) and a query that yields nothing is the answer the
    // adapter already gives a revoked grant (INV-TREE-4). The partial pool is discarded by the
    // loader on the main thread and never reaches the screen.
    //
    // Every boundary, not just the middle: abandoned before the root is listed, after the root but
    // before any images, part-way, and never abandoned at all.
    [Theory]
    [InlineData(0, new string[0])]
    [InlineData(1, new string[0])]
    [InlineData(2, new[] { "a1", "a2" })]
    [InlineData(3, new[] { "a1", "a2", "b1", "b2" })]
    public void ATreeThatStopsAnswering_YieldsWhateverItGotTo(int answerFirst, string[] expected)
    {
        var tree = new StopsAnsweringTree(answerFirst);

        var library = new ReferenceLibrary(tree, "root");

        Assert.Equal(expected, library.Pool);
        Assert.Equal(Math.Min(answerFirst, 3), tree.Walks);
    }

    // Answers the first N GetChildren calls and nothing afterwards, standing in for a walk whose
    // load was superseded midway through.
    sealed class StopsAnsweringTree(int answerFirst) : IDocumentTree
    {
        static readonly Dictionary<string, DocumentEntry[]> Children = new()
        {
            ["root"] = [Folder("a"), Folder("b")],
            ["a"] = [Img("a1"), Img("a2")],
            ["b"] = [Img("b1"), Img("b2")],
        };

        public int Walks { get; private set; }

        public IEnumerable<DocumentEntry> GetChildren(string parentDocumentId)
        {
            if (Walks >= answerFirst)
                return [];

            Walks++;
            return Children.TryGetValue(parentDocumentId, out var kids) ? kids : [];
        }
    }

    [Fact]
    public void Empty_HasNoRootAndNoImages()
    {
        var empty = ReferenceLibrary.Empty;

        Assert.True(empty.IsEmpty);
        Assert.Empty(empty.Pool);
        Assert.Equal(string.Empty, empty.RootDocumentId);

        // And a second Empty is just as empty — the no-folder state has no walk behind it to redo.
        Assert.Empty(ReferenceLibrary.Empty.Pool);
    }

    // A session copies the pool at construction, so re-walking the folder mid-session cannot
    // reorder or resize the run already under way (INV-POOL-4).
    [Fact]
    public void ReLoadingTheFolder_DoesNotDisturbARunningSession()
    {
        var tree = new FakeTree(new() { ["root"] = [Img("a"), Img("b")] });
        var library = new ReferenceLibrary(tree, "root");

        var session = new DrawingSession<string>(
            library.Pool, new SessionConfig(30, 4), id => id,
            shuffle: false, random: new Random(1), clock: () => TimeSpan.Zero);

        // The screen re-loads by building a new library over the edited tree, exactly as the loader
        // does on every return to the screen. The running session drew from a copy of the pool.
        tree.Replace("root", Img("z"), Img("y"), Img("x"));
        var reloaded = new ReferenceLibrary(tree, "root");

        var seen = new List<string>();
        while (!session.IsComplete)
        {
            seen.Add(session.CurrentImage!);
            session.Next();
        }

        Assert.Equal(new[] { "a", "b", "a", "b" }, seen);

        // The new library sees the edit; the one the session drew from is untouched, which is what
        // makes the swap safe to do while a session is running (INV-POOL-4).
        Assert.Equal(new[] { "z", "y", "x" }, reloaded.Pool);
        Assert.Equal(new[] { "a", "b" }, library.Pool);
    }

    static ReferenceLibrary LibraryOf(int imageCount)
    {
        var images = new DocumentEntry[imageCount];
        for (var i = 0; i < imageCount; i++)
            images[i] = Img($"img{i:0000}");

        return new ReferenceLibrary(new FakeTree(new() { ["root"] = images }), "root");
    }

    [Fact]
    public void Sample_ReturnsTheWholePoolWhenItFitsTheBound()
    {
        var library = LibraryOf(5);

        Assert.Equal(library.Pool, library.Sample(5, new Random(1)));
        Assert.Equal(library.Pool, library.Sample(50, new Random(1)));
    }

    [Fact]
    public void Sample_BoundsTheHandoffWithoutTruncatingTheLibrary()
    {
        var library = LibraryOf(100);

        var handoff = library.Sample(10, new Random(7));

        Assert.Equal(10, handoff.Count);
        Assert.Equal(100, library.Count);
    }

    [Fact]
    public void Sample_KeepsEnumerationOrderAndUniqueness()
    {
        var library = LibraryOf(100);

        var handoff = library.Sample(20, new Random(3));

        Assert.Equal(handoff.Distinct().Count(), handoff.Count);
        Assert.Equal(handoff.OrderBy(id => library.Pool.ToList().IndexOf(id)), handoff);
    }

    // A truncating handoff would only ever report the first ids the walk reached, and a biased one
    // would favour part of the pool. Over 200 seeds every index must come up, and none of them
    // anywhere near often enough to suggest the shuffle is lopsided.
    [Fact]
    public void Sample_DrawsUniformlyFromTheWholePool()
    {
        var library = LibraryOf(100);
        var index = library.Pool.Select((id, i) => (id, i)).ToDictionary(x => x.id, x => x.i);
        var hits = new int[100];

        for (var seed = 0; seed < 200; seed++)
        {
            foreach (var id in library.Sample(10, new Random(seed)))
                hits[index[id]]++;
        }

        Assert.DoesNotContain(hits, count => count == 0);

        // Mean is 200*10/100 = 20 draws per index.
        Assert.DoesNotContain(hits, count => count > 60);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(50)]
    [InlineData(99)]   // the boundary: the partial shuffle runs to the end of the index array
    [InlineData(100)]
    public void Sample_ReturnsExactlyTheRequestedCountOfPoolMembers(int maxIds)
    {
        var library = LibraryOf(100);

        var handoff = library.Sample(maxIds, new Random(5));

        Assert.Equal(maxIds, handoff.Count);
        Assert.All(handoff, id => Assert.Contains(id, library.Pool));
    }

    // The overload the app actually calls takes no Random.
    [Fact]
    public void Sample_WithoutAnInjectedRandom_StillBoundsTheHandoff()
    {
        var library = LibraryOf(100);

        var handoff = library.Sample(10);

        Assert.Equal(10, handoff.Count);
        Assert.All(handoff, id => Assert.Contains(id, library.Pool));
    }

    // A count is not a size: ids carry the whole document path, so the handoff is bounded in
    // characters as well (INV-POOL-6).
    [Fact]
    public void Sample_StopsAtTheCharacterBudget()
    {
        var library = LibraryOf(100);           // ids are "img0000" — 7 characters each
        var handoff = library.Sample(100, 70, new Random(9));

        Assert.Equal(10, handoff.Count);
    }

    [Fact]
    public void Sample_WithinTheCharacterBudget_ReturnsTheWholePool()
    {
        var library = LibraryOf(10);

        Assert.Equal(library.Pool, library.Sample(10, 1000, new Random(9)));
    }

    // One pathological id must still yield a usable pool rather than an empty one.
    [Fact]
    public void Sample_AlwaysTakesAtLeastOneId()
    {
        var library = LibraryOf(100);

        Assert.Single(library.Sample(100, 1, new Random(9)));
    }

    [Fact]
    public void Sample_YieldsNothingForANonPositiveCharacterBudget() =>
        Assert.Empty(LibraryOf(10).Sample(10, 0, new Random(1)));

    [Fact]
    public void Sample_IsDeterministicForAGivenRandom() =>
        Assert.Equal(LibraryOf(100).Sample(15, new Random(42)),
                     LibraryOf(100).Sample(15, new Random(42)));

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Sample_YieldsNothingForANonPositiveBound(int maxIds) =>
        Assert.Empty(LibraryOf(10).Sample(maxIds, new Random(1)));

    [Fact]
    public void Sample_OfAnEmptyLibraryIsEmpty() =>
        Assert.Empty(ReferenceLibrary.Empty.Sample(10, new Random(1)));

    // A provider that throws is the state a remembered folder hits when its volume is unmounted or
    // its provider is gone — the screen catches it and shows the remembered-but-unreachable message.
    // What must not happen is a half-built pool surviving underneath that message: Start would open
    // on images the session cannot read (INV-POOL-1, INV-X-11).
    sealed class FailableTree : IDocumentTree
    {
        public bool Failing { get; set; }

        public IEnumerable<DocumentEntry> GetChildren(string parentDocumentId)
        {
            if (Failing)
                throw new InvalidOperationException("provider gone");

            yield return new DocumentEntry("a.jpg", "image/jpeg");
            yield return new DocumentEntry("b.jpg", "image/jpeg");
        }
    }

    [Fact]
    public void ATreeThatThrowsAtTheRoot_NeverBuildsALibrary()
    {
        var tree = new FailableTree { Failing = true };

        Assert.Throws<InvalidOperationException>(() => new ReferenceLibrary(tree, "root"));
    }

    [Fact]
    public void ATreeThatStartsThrowing_LeavesThePreviousPoolIntact()
    {
        var tree = new FailableTree();
        var library = new ReferenceLibrary(tree, "root");
        var pool = library.Pool;

        Assert.Equal(2, pool.Count);

        tree.Failing = true;

        // A re-walk is a *new* library, not a second walk on the live one (#4 made Enumerate
        // private for exactly this reason), so a provider that starts throwing cannot reach the pool
        // the screen is already showing: the failure happens while building the replacement, and the
        // replacement is never assigned. The screen catches it and shows the unavailable message,
        // with the pool it had still whole underneath (INV-POOL-1, INV-X-11).
        Assert.Throws<InvalidOperationException>(() => new ReferenceLibrary(tree, "root"));
        Assert.Same(pool, library.Pool);
        Assert.Equal(2, library.Count);
    }
}
