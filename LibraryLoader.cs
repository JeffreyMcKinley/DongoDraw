using Android.Content;
using Android.Database;
using Android.Graphics;
using Android.Provider;
using Android.Util;
using DongoDraw.Core;

namespace DongoDraw
{
    sealed class LibraryLoad(ReferenceLibrary library, List<Bitmap?> thumbnails)
    {
        public ReferenceLibrary Library { get; } = library;

        public List<Bitmap?> Thumbnails { get; } = thumbnails;
    }

    // The application ContentResolver, never an Activity's — that would pin its view tree.
    sealed class LibraryLoader(ContentResolver resolver)
    {
        const string LogTag = "DongoDraw";

        readonly LoadGeneration generation = new();

        // Also ResetLibrary; never OnPause, or a brief background comes back to an empty library.
        public void Abandon() => generation.Abandon();

        bool IsCurrent(int ticket) => generation.IsCurrent(ticket);

        public async Task<LibraryLoad?> LoadAsync(Android.Net.Uri treeUri, int maxThumbnails,
            int thumbnailDimension, int maxThumbnailDimension)
        {
            var mine = generation.Take();

            // Declared out here so a throw out of the worker still leaves it reachable to free.
            var decoded = new List<Bitmap?>();

            try
            {
                // Inside the try: hoisted out, this throw escapes the superseded check (INV-X-13).
                var rootDocumentId = DocumentsContract.GetTreeDocumentId(treeUri)
                    ?? throw new InvalidOperationException("The picked tree has no document id.");

                var library = await Task.Run(() => WalkAndDecode(
                    resolver, treeUri, rootDocumentId, decoded, maxThumbnails,
                    thumbnailDimension, maxThumbnailDimension, () => IsCurrent(mine)));

                if (!IsCurrent(mine))
                {
                    DiscardThumbnails(decoded);
                    return null;
                }

                return new LibraryLoad(library, decoded);
            }
            catch (Exception error)
            {
                DiscardThumbnails(decoded);

                if (!IsCurrent(mine))
                {
                    Log.Warn(LogTag, $"Abandoned load failed after being superseded: {error.Message}");
                    return null;
                }

                throw;
            }
        }

        static ReferenceLibrary WalkAndDecode(
            ContentResolver resolver, Android.Net.Uri treeUri, string rootDocumentId,
            List<Bitmap?> decoded, int maxThumbnails, int thumbnailDimension, int maxThumbnailDimension,
            Func<bool> isCurrent)
        {
            var tree = new ContentResolverDocumentTree(resolver, treeUri, isCurrent);

            // Runs per image FOUND, not per image previewed: the using is load-bearing (§8).
            var library = new ReferenceLibrary(
                tree,
                rootDocumentId,
                treeUri.LastPathSegment,
                documentId =>
                {
                    using var documentUri = DocumentsContract.BuildDocumentUriUsingTree(treeUri, documentId);
                    return documentUri?.ToString();
                });

            // Bounds decode ATTEMPTS, not successes, so unreadable files cannot cost a trip each.
            foreach (var id in library.Pool.Take(maxThumbnails))
            {
                if (!isCurrent())
                {
                    DiscardThumbnails(decoded);
                    break;
                }

                using var fileUri = Android.Net.Uri.Parse(id);
                if (fileUri is not null &&
                    DecodeThumbnail(resolver, fileUri, thumbnailDimension, maxThumbnailDimension) is { } bitmap)
                    decoded.Add(bitmap);
            }

            return library;
        }

        static Bitmap? DecodeThumbnail(
            ContentResolver resolver, Android.Net.Uri uri, int thumbnailDimension, int maxThumbnailDimension)
        {
            try
            {
                return ImageDecoding.DecodeSampledBitmap(
                    resolver, uri, thumbnailDimension, maxThumbnailDimension);
            }
            catch (Exception ex)
            {
                Log.Warn(LogTag, $"Skipping image {uri}: {ex.Message}");
                return null;
            }
        }

        public static void DiscardThumbnails(List<Bitmap?> bitmaps)
        {
            foreach (var bitmap in bitmaps)
            {
                // A cleared slot means a view owns those pixels; freeing it recycles the grid's.
                if (bitmap is null)
                    continue;

                if (!bitmap.IsRecycled)
                    bitmap.Recycle();

                bitmap.Dispose();
            }

            bitmaps.Clear();
        }

        sealed class ContentResolverDocumentTree(
            ContentResolver resolver, Android.Net.Uri treeUri, Func<bool> isCurrent) : IDocumentTree
        {
            // Abandonment checked here, not on the port: it is indistinguishable from a revoked
            // query (INV-TREE-4), so no threading concept crosses into Core.
            public IEnumerable<DocumentEntry> GetChildren(string parentDocumentId)
            {
                var entries = new List<DocumentEntry>();

                if (!isCurrent())
                    return entries;

                try
                {
                    using var childrenUri =
                        DocumentsContract.BuildChildDocumentsUriUsingTree(treeUri, parentDocumentId);

                    using ICursor? cursor = resolver.Query(
                        childrenUri!,
                        new[]
                        {
                            DocumentsContract.Document.ColumnDocumentId,
                            DocumentsContract.Document.ColumnMimeType,
                        },
                        null, null, null);

                    if (cursor is null)
                        return entries;

                    try
                    {
                        while (cursor.MoveToNext())
                        {
                            if (!isCurrent())
                                break;

                            var documentId = cursor.GetString(0);
                            if (documentId is null)
                                continue;

                            entries.Add(new DocumentEntry(documentId, cursor.GetString(1)));
                        }
                    }
                    finally
                    {
                        cursor.Close();
                    }
                }
                catch (Exception error)
                {
                    Log.Warn(LogTag, $"Listing {parentDocumentId} failed: {error.Message}");
                }

                return entries;
            }
        }
    }
}
