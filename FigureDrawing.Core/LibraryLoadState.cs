namespace FigureDrawing.Core;

public static class LibraryLoadState
{
    public static bool KeepsPool(string? loadedTreeUri, string? next) =>
        loadedTreeUri is not null && next is not null &&
        string.Equals(loadedTreeUri, next, StringComparison.Ordinal);
}
