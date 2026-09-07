namespace FigureDrawing.Core;

public readonly record struct PersistedGrant(string? Reference, bool IsRead);

public enum LibraryStatus
{
    NeverPicked,
    Unavailable,
    Empty,
    Ready,
}

// The rules for deciding what a remembered reference is still worth acting on (INV-REF-*).
public static class LibraryReference
{
    const string TreeSegment = "/tree/";

    const string ContentScheme = "content://";

    public const int MaxLength = 4096;

    public static bool TryParse(string? stored, out string reference)
    {
        reference = string.Empty;

        if (string.IsNullOrWhiteSpace(stored))
            return false;

        var trimmed = stored.Trim();

        if (trimmed.Length > MaxLength)
            return false;

        if (!trimmed.StartsWith(ContentScheme, StringComparison.OrdinalIgnoreCase))
            return false;

        // Searched from past the scheme's own "//" so the authority cannot supply the segment:
        // "content://tree/x" has none, and "content:///tree/x" has no authority to own one.
        var authority = ContentScheme.Length;
        var tree = trimmed.IndexOf(TreeSegment, authority, StringComparison.Ordinal);

        if (tree <= authority || tree + TreeSegment.Length >= trimmed.Length)
            return false;

        reference = Canonical(trimmed);
        return true;
    }

    public static bool HasReadGrant(string? reference, IEnumerable<PersistedGrant> grants)
    {
        ArgumentNullException.ThrowIfNull(grants);

        if (string.IsNullOrWhiteSpace(reference))
            return false;

        var wanted = Canonical(reference.Trim());

        foreach (var grant in grants)
        {
            if (grant.IsRead && Matches(grant.Reference, wanted))
                return true;
        }

        return false;
    }

    public static IReadOnlyList<string> GrantsToRelease(string? keep, IEnumerable<PersistedGrant> grants)
    {
        ArgumentNullException.ThrowIfNull(grants);

        var kept = string.IsNullOrWhiteSpace(keep) ? null : Canonical(keep.Trim());
        var stale = new List<string>();

        foreach (var grant in grants)
        {
            if (!grant.IsRead || string.IsNullOrWhiteSpace(grant.Reference))
                continue;

            if (kept is not null && Matches(grant.Reference, kept))
                continue;

            // As reported, not canonicalised: this string identifies the grant (INV-REF-4).
            stale.Add(grant.Reference);
        }

        return stale;
    }

    public static LibraryStatus Classify(
        string? stored,
        IEnumerable<PersistedGrant> grants,
        int imageCount,
        bool walkFailed = false)
    {
        ArgumentNullException.ThrowIfNull(grants);

        if (!TryParse(stored, out var reference))
            return LibraryStatus.NeverPicked;

        if (walkFailed || !HasReadGrant(reference, grants))
            return LibraryStatus.Unavailable;

        return imageCount > 0 ? LibraryStatus.Ready : LibraryStatus.Empty;
    }

    static bool Matches(string? candidate, string canonicalWanted) =>
        candidate is not null &&
        string.Equals(Canonical(candidate.Trim()), canonicalWanted, StringComparison.Ordinal);

    static string Canonical(string reference)
    {
        var characters = reference.ToCharArray();

        for (var i = 0; i < characters.Length; i++)
        {
            if (i < ContentScheme.Length && characters[i] != ':')
            {
                characters[i] = char.ToLowerInvariant(characters[i]);
                continue;
            }

            if (characters[i] == '%' && i + 2 < characters.Length &&
                IsHex(characters[i + 1]) && IsHex(characters[i + 2]))
            {
                characters[i + 1] = char.ToUpperInvariant(characters[i + 1]);
                characters[i + 2] = char.ToUpperInvariant(characters[i + 2]);
                i += 2;
            }
        }

        return new string(characters);
    }

    static bool IsHex(char c) =>
        c is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F';
}
