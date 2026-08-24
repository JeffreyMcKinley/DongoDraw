namespace FigureDrawing.Core;

// When a load may keep the pool the screen is already showing.
//
// Android-free and deterministic, so it lives here for the same reason as LoadGeneration: it is a
// comparison, and a comparison read from source text cannot be told apart from its own inverse. Get
// it wrong and Start stays armed over a pool belonging to a different folder — the "never a mixture"
// clause of INV-X-13 — which is not a failure any source-shape test can see.
//
// Not a domain object: it holds no rule about what a library *is*, and names nothing in the
// ubiquitous language. See ARCHITECTURE.md §3.
public static class LibraryLoadState
{
    // Whether a load of `next` may keep the pool loaded from `loadedTreeUri`.
    //
    // Whole tree uris, never bare document ids: an id like "primary:Pictures" is not unique across
    // providers, so a same-named folder on an SD card or a cloud provider would otherwise inherit
    // the previous provider's pool — and its armed Start — under the new folder's name.
    //
    // A null on either side means "no folder": nothing loaded yet, so a first pick keeps nothing —
    // and a tree that cannot even be named is never the folder already on screen.
    public static bool KeepsPool(string? loadedTreeUri, string? next) =>
        loadedTreeUri is not null && next is not null &&
        string.Equals(loadedTreeUri, next, StringComparison.Ordinal);
}
