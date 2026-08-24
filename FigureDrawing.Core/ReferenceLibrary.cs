namespace FigureDrawing.Core;

// One entry (file or subfolder) inside a picked folder tree, reduced to the two fields the
// enumeration cares about. The Android layer maps a Storage Access Framework document row to
// this; tests supply it directly.
public readonly record struct DocumentEntry(string DocumentId, string? MimeType);

// Abstraction over "list the direct children of a folder document". The Android adapter backs
// this with DocumentsContract + ContentResolver; tests back it with an in-memory tree.
//
// This is the anti-corruption layer, so it stays a separate port rather than folding into
// ReferenceLibrary: DocumentsContract, ContentResolver, Cursor and Uri stop at the adapter that
// implements it (INV-TREE-1).
public interface IDocumentTree
{
    IEnumerable<DocumentEntry> GetChildren(string parentDocumentId);
}

// The reference library: the folder the artist picked, everything drawable beneath it, and the walk
// that discovers them (docs/DOMAIN-MODEL.md §2.2). One object, because the pool is the library's
// contents and the traversal is how it computes them — neither had a lifetime of its own.
//
// Membership is derived, never stored (INV-GRP-1): the pool is whatever the document tree reports
// now, so a folder the user edited between launches is picked up by re-enumerating rather than by
// migrating a persisted list. Only the root's identity is persisted (Settings.LastCollection).
//
// There is no IsAvailable here on purpose: whether a persisted read permission is still held is
// Android knowledge, and a revoked grant is already covered — the tree reports nothing, the library
// enumerates to empty, and the empty state shows (INV-GRP-4, INV-GRP-5).
public sealed class ReferenceLibrary
{
    // MIME type the Storage Access Framework reports for a subdirectory in a tree.
    public const string DirectoryMimeType = "vnd.android.document/directory";

    // Depth ceiling for the walk. Deeper than any real photo library, shallow enough that a
    // provider synthesizing an endless chain of folders stops rather than overflowing the stack.
    const int MaxDepth = 64;

    readonly IDocumentTree? _tree;
    readonly Func<string, string?> _toImageId;

    // tree           : the document tree to walk.
    // rootDocumentId : the picked folder, and the library's identity.
    // displayName    : what to call it on screen; null when the picker gave no name.
    // toImageId      : maps a document id to the durable id a session draws from. The Android layer
    //                  passes DocumentsContract.BuildDocumentUriUsingTree(...) so the pool holds
    //                  content URIs; the default is identity, which is what lets tests use
    //                  "a"/"b"/"c" (INV-IMG-1). Returning null drops the entry.
    public ReferenceLibrary(
        IDocumentTree tree,
        string rootDocumentId,
        string? displayName = null,
        Func<string, string?>? toImageId = null)
    {
        ArgumentNullException.ThrowIfNull(tree);
        ArgumentNullException.ThrowIfNull(rootDocumentId);

        _tree = tree;
        _toImageId = toImageId ?? (id => id);
        RootDocumentId = rootDocumentId;
        DisplayName = displayName;

        Enumerate();
    }

    // The library before a folder has been picked: no root, no images, and Start stays shut.
    ReferenceLibrary()
    {
        _toImageId = id => id;
        RootDocumentId = string.Empty;
    }

    // No folder picked yet. A first run shows this, and so does a launch whose persisted permission
    // has been revoked. A fresh instance rather than a shared singleton: cheap, and it keeps the
    // empty library from being process-wide state two screens could come to share.
    public static ReferenceLibrary Empty => new();

    // The tree URI / document id of the picked folder. Stable across launches — that is what makes
    // restore possible.
    public string RootDocumentId { get; }

    // What to call this library on screen, when the picker gave a name.
    public string? DisplayName { get; }

    // The ordered image ids one session may draw from: fixed order (INV-POOL-1), no duplicates
    // (INV-POOL-2), and copied rather than aliased when a session starts (INV-POOL-4).
    public IReadOnlyList<string> Pool { get; private set; } = [];

    public int Count => Pool.Count;

    // Zero images is normal, not an error (INV-GRP-4): it shows the empty state and blocks Start.
    public bool IsEmpty => Pool.Count == 0;

    // Walk the tree. The constructor is the only caller, and it is private so that stays true:
    // rebuilding Pool in place would let a screen already reading the pool see it half-built
    // (INV-X-13), and a rule enforced by the compiler beats one enforced by a comment.
    //
    // A *re*-walk is a fresh ReferenceLibrary, swapped in whole. Membership is still re-derived on
    // every load (INV-GRP-1) — by a new instance rather than by mutating the live one.
    void Enumerate()
    {
        if (_tree is null)
        {
            Pool = [];
            return;
        }

        var images = new List<string>();
        var seen = new HashSet<string>();
        var visited = new HashSet<string>();

        Walk(RootDocumentId, images, seen, visited, depth: 0);

        // Read-only so a caller cannot cast the pool back to List<string> and reorder it under a
        // running session (INV-POOL-1, INV-POOL-4).
        Pool = images.AsReadOnly();
    }

    // The pool, bounded for a handoff that cannot carry all of it (INV-POOL-6). The whole pool is
    // returned whenever it fits; past the bound a uniform random sample is taken so a 10,000-image
    // library is not silently reduced to whatever the walk happened to reach first. Enumeration
    // order is preserved within the sample, so the session's shuffle setting still decides the
    // order it draws in — this decides only which images are in play.
    //
    // maxIds : upper bound on the number of ids, 0 or less yields an empty pool.
    // random : injectable for deterministic tests; defaults to a shared Random.
    public IReadOnlyList<string> Sample(int maxIds, Random? random = null) =>
        Sample(maxIds, int.MaxValue, random);

    // The same bound, expressed in characters as well as in ids. A count alone does not bound the
    // size of the handoff: a document id carries the whole relative path, so a deep tree with long
    // filenames runs several hundred characters per id where a shallow one runs fifty. A caller
    // whose limit is really a byte budget passes it here and gets however many ids fit, whole.
    //
    // maxTotalIdLength : upper bound on the summed length of the ids returned. The first id is
    //                    always taken when maxIds allows it, so a single pathological id yields a
    //                    one-image pool rather than an empty one.
    public IReadOnlyList<string> Sample(int maxIds, int maxTotalIdLength, Random? random = null)
    {
        if (maxIds <= 0 || maxTotalIdLength <= 0)
            return [];

        if (Pool.Count <= maxIds && TotalIdLength(Pool) <= maxTotalIdLength)
            return Pool;

        var take = Math.Min(maxIds, Pool.Count);
        var picker = random ?? Random.Shared;

        // Partial Fisher-Yates over the indices: the first `take` entries end up a uniform sample
        // without shuffling (or copying) the whole pool.
        var indices = new int[Pool.Count];
        for (var i = 0; i < indices.Length; i++)
            indices[i] = i;

        for (var i = 0; i < take; i++)
        {
            var j = picker.Next(i, indices.Length);
            (indices[i], indices[j]) = (indices[j], indices[i]);
        }

        // Sort only the chosen prefix, in place: the sample comes back in enumeration order.
        Array.Sort(indices, 0, take);

        var sample = new List<string>(take);
        var length = 0L;

        for (var i = 0; i < take; i++)
        {
            var id = Pool[indices[i]];
            length += id.Length;

            if (sample.Count > 0 && length > maxTotalIdLength)
                break;

            sample.Add(id);
        }

        return sample.AsReadOnly();
    }

    static long TotalIdLength(IReadOnlyList<string> ids)
    {
        var total = 0L;
        foreach (var id in ids)
            total += id.Length;

        return total;
    }

    public static bool IsDirectory(string? mimeType) => mimeType == DirectoryMimeType;

    // Any tree entry whose MIME type starts with "image/" (jpg/png/webp/gif/heic/...). A
    // directory is never an image even though nothing else claims that MIME prefix (INV-IMG-3).
    // Ordinal: the MIME string comes from an untrusted provider, and a culture-sensitive comparison
    // treats some characters as ignorable — "­image/png" would pass, and differently per locale.
    public static bool IsImage(string? mimeType) =>
        mimeType is not null && !IsDirectory(mimeType) &&
        mimeType.StartsWith("image/", StringComparison.Ordinal);

    // Depth-first walk from the root, descending into every subdirectory and collecting the image
    // ids in encounter order (INV-GRP-2, INV-GRP-6). The visited set guards against cycles — a tree
    // that reports a document as its own descendant must not loop forever (INV-GRP-3).
    void Walk(
        string documentId,
        List<string> images,
        HashSet<string> seen,
        HashSet<string> visited,
        int depth)
    {
        // The visited set stops a provider that reports a *cycle*. A provider that synthesizes a
        // fresh id at every level never repeats one, so depth is bounded separately — this walk
        // recurses, and a stack overflow kills the process rather than raising something catchable.
        // Stopping early yields a partial pool, which is an ordinary outcome (INV-GRP-4).
        if (_tree is null || depth > MaxDepth || !visited.Add(documentId))
            return;

        foreach (var entry in _tree.GetChildren(documentId))
        {
            if (entry.DocumentId is null)
                continue;

            if (IsDirectory(entry.MimeType))
            {
                Walk(entry.DocumentId, images, seen, visited, depth + 1);
            }
            else if (IsImage(entry.MimeType))
            {
                // The same image reached twice (two subfolders reporting one document) is one entry
                // in the pool, never two (INV-POOL-2).
                if (MapId(entry.DocumentId) is { } id && seen.Add(id))
                    images.Add(id);
            }
        }
    }

    // The mapper is supplied by the Android adapter and can fail on a document id the provider
    // reports from outside the tree. An entry that cannot be mapped is dropped, exactly like one
    // the mapper deliberately rejects — a bad row never aborts the walk (INV-X-11).
    string? MapId(string documentId)
    {
        try
        {
            return _toImageId(documentId);
        }
        catch (Exception)
        {
            return null;
        }
    }
}
