namespace DongoDraw.Core;

public readonly record struct DocumentEntry(string DocumentId, string? MimeType);

// The port for listing the direct children of a folder document (INV-TREE-1).
public interface IDocumentTree
{
    IEnumerable<DocumentEntry> GetChildren(string parentDocumentId);
}

public sealed class ReferenceLibrary
{
    public const string DirectoryMimeType = "vnd.android.document/directory";

    const int MaxDepth = 64;

    readonly IDocumentTree? _tree;
    readonly Func<string, string?> _toImageId;

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

    ReferenceLibrary()
    {
        _toImageId = id => id;
        RootDocumentId = string.Empty;
    }

    public static ReferenceLibrary Empty => new();

    public string RootDocumentId { get; }

    public string? DisplayName { get; }

    public IReadOnlyList<string> Pool { get; private set; } = [];

    public int Count => Pool.Count;

    public bool IsEmpty => Pool.Count == 0;

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

        Pool = images.AsReadOnly();
    }

    public IReadOnlyList<string> Sample(int maxIds, Random? random = null) =>
        Sample(maxIds, int.MaxValue, random);

    public IReadOnlyList<string> Sample(int maxIds, int maxTotalIdLength, Random? random = null)
    {
        if (maxIds <= 0 || maxTotalIdLength <= 0)
            return [];

        if (Pool.Count <= maxIds && TotalIdLength(Pool) <= maxTotalIdLength)
            return Pool;

        var take = Math.Min(maxIds, Pool.Count);
        var picker = random ?? Random.Shared;

        // Partial Fisher-Yates: the first `take` indices are a uniform sample without shuffling
        // or copying the whole pool.
        var indices = new int[Pool.Count];
        for (var i = 0; i < indices.Length; i++)
            indices[i] = i;

        for (var i = 0; i < take; i++)
        {
            var j = picker.Next(i, indices.Length);
            (indices[i], indices[j]) = (indices[j], indices[i]);
        }

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

    // Ordinal because the MIME string comes from an untrusted provider: a culture-sensitive
    // comparison treats some characters as ignorable, so "­image/png" would pass, per locale.
    public static bool IsImage(string? mimeType) =>
        mimeType is not null && !IsDirectory(mimeType) &&
        mimeType.StartsWith("image/", StringComparison.Ordinal);

    void Walk(
        string documentId,
        List<string> images,
        HashSet<string> seen,
        HashSet<string> visited,
        int depth)
    {
        // Depth is bounded separately from the cycle guard, not redundantly with it: a provider
        // that mints a fresh id per level never repeats one (INV-GRP-3).
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
                if (MapId(entry.DocumentId) is { } id && seen.Add(id))
                    images.Add(id);
            }
        }
    }

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
