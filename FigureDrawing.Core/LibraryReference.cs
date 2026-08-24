namespace FigureDrawing.Core;

// One entry from the platform's list of permissions the app still holds, reduced to the two fields
// the decisions below need. The Android layer maps a persisted URI permission to this; tests supply
// it directly.
public readonly record struct PersistedGrant(string? Reference, bool IsRead);

// What the library pane has to say for itself, decided from the remembered reference, the grants
// held, and what the walk found (INV-REF-5). Four states, because they are four different things to
// tell the artist and only one of them is "nothing here".
public enum LibraryStatus
{
    // No folder has ever been picked, or what is stored is not a reference this app can use.
    NeverPicked,

    // A folder is remembered, and cannot be opened: the read grant is gone, or the walk failed.
    Unavailable,

    // A folder is remembered, readable, and holds no images (INV-GRP-4).
    Empty,

    // A folder is remembered, readable, and holds images.
    Ready,
}

// The rules about the *remembered* reference library — the string `Settings.LastCollection` carries
// between launches (docs/DOMAIN-MODEL.md §2.4, `INV-REF-*`).
//
// Pure, and previously answered inside MainActivity where only a text-matching contract test could
// reach them: is this stored value still worth acting on, do we still hold the permission that made
// it usable, which permissions are we holding that we no longer want, and what should the screen
// say. What the answers are *used for* stays in the Android layer.
//
// The SAF *form* is domain knowledge here, deliberately (`INV-TREE-1` carve-out): this type
// recognises the shape of the reference the app persisted and never constructs one, never resolves
// one, and never names a DocumentsContract, ContentResolver, Cursor or Uri.
//
// A stored value is expected to go bad (`INV-GRP-5`): the folder may be gone, the grant revoked, or
// the value itself written by a version of this app that stored something else. Every one of those
// is an ordinary "no", never an exception.
public static class LibraryReference
{
    // The Storage Access Framework's tree form. Anything else — a bare document URI, a file path, a
    // value from another app — cannot be turned into a tree document id, and asking the platform to
    // do it anyway throws rather than returning null.
    const string TreeSegment = "/tree/";

    // SAF hands out content:// URIs and nothing else. Checked because the value is read back from
    // storage rather than from the picker that produced it.
    const string ContentScheme = "content://";

    // Ceiling on a stored reference, in characters. A reference is one authority plus one document
    // id, so a few thousand characters is already far past any real folder; the bound is here
    // because this value is read back from a file and then handed to the system picker in a Binder
    // transaction, which is the same reason the pool handoff is bounded (`INV-POOL-6`).
    public const int MaxLength = 4096;

    // Whether `stored` is a reference this app could still act on, and the canonical form to act on.
    // Null, blank, over-long, non-content, and non-tree values are all a plain "no" (`INV-REF-1`).
    // Whether the folder still EXISTS is not knowable here and is not asked: that shows up as an
    // empty enumeration, which is a normal outcome (`INV-GRP-4`).
    public static bool TryParse(string? stored, out string reference)
    {
        reference = string.Empty;

        if (string.IsNullOrWhiteSpace(stored))
            return false;

        var trimmed = stored.Trim();

        if (trimmed.Length > MaxLength)
            return false;

        // RFC 3986 makes the scheme case-insensitive, so it is matched — and later canonicalised —
        // that way. The document id after it is opaque and is never case-folded (`INV-IMG-1`).
        if (!trimmed.StartsWith(ContentScheme, StringComparison.OrdinalIgnoreCase))
            return false;

        // The search starts after the scheme's own "//", so a provider whose authority is literally
        // "tree" cannot supply the segment: "content://tree/x" has no tree segment at all, and
        // "content:///tree/x" has no authority to own one.
        var authority = ContentScheme.Length;
        var tree = trimmed.IndexOf(TreeSegment, authority, StringComparison.Ordinal);

        if (tree <= authority || tree + TreeSegment.Length >= trimmed.Length)
            return false;

        reference = Canonical(trimmed);
        return true;
    }

    // Whether the app still holds a read grant for this reference. A grant that was taken as
    // persistable can still be gone — permissions cleared, volume unmounted, provider uninstalled,
    // or the platform trimming the oldest of a package's grants — and acting on the reference
    // without one fails at the provider instead (`INV-REF-3`, `INV-GRP-5`).
    //
    // Write grants are not accepted as read grants: this app only ever takes the read flag, so a
    // write-only entry means someone else's grant, not ours.
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

    // The read grants held for folders that are no longer the remembered one (`INV-REF-4`). A
    // package's persisted grants are capped and the platform drops the OLDEST past the cap, so a
    // grant kept for a folder the app will never open again is a grant that can cost it the one it
    // still uses. Re-picking the same folder releases nothing.
    //
    // keep : the reference to hold on to, or null/blank to release everything held.
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

            // Returned as reported, not canonicalised: this is handed back to the platform to
            // release, and it is the platform's own spelling that identifies the grant.
            stale.Add(grant.Reference);
        }

        return stale;
    }

    // What the library pane is looking at (`INV-REF-5`). The screen maps this to a message; it does
    // not decide it.
    //
    // imageCount : what the walk found, or 0 when it did not run.
    // walkFailed : the walk was attempted and threw — a remembered folder that cannot be read is
    //              unavailable even when the grant is still listed.
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

    // Two references naming the same folder. The comparison is over the canonical form because the
    // two sides come from different platform round-trips — the string the picker returned, stored
    // months ago, against the string the grant list reports today — and those agree on the folder
    // while differing on how they spell it.
    static bool Matches(string? candidate, string canonicalWanted) =>
        candidate is not null &&
        string.Equals(Canonical(candidate.Trim()), canonicalWanted, StringComparison.Ordinal);

    // Lower-cases the scheme and upper-cases percent-escapes, leaving everything else byte-exact.
    // Those are the two differences RFC 3986 calls equivalent and providers are inconsistent about
    // ("%3A" vs "%3a" in a document id is the common one); the document id's own characters are
    // opaque and are never touched.
    static string Canonical(string reference)
    {
        var characters = reference.ToCharArray();

        for (var i = 0; i < characters.Length; i++)
        {
            // The scheme runs to the first ':', which is also where the ':' itself sits.
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
