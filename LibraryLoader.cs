using Android.Content;
using Android.Database;
using Android.Graphics;
using Android.Provider;
using Android.Util;
using FigureDrawing.Core;

namespace FigureDrawing
{
    // One completed load: the pool the walk found, and the previews it decoded for the grid.
    //
    // Android-side by design. It carries decoded Bitmaps, which is exactly why it is not a Core type
    // (INV-X-4) — and why ownership of them has to be unambiguous: between the decode and the
    // moment the screen attaches them to ImageViews, this object is their only owner.
    sealed class LibraryLoad(ReferenceLibrary library, List<Bitmap?> thumbnails)
    {
        public ReferenceLibrary Library { get; } = library;

        // In pool order, and at most the cap the caller asked for. Handing these to a view transfers
        // ownership; anything still here afterwards is the caller's to release.
        public List<Bitmap?> Thumbnails { get; } = thumbnails;
    }

    // Reads a reference library off the UI thread: the recursive Storage Access Framework walk (one
    // blocking provider query per folder) plus the preview decodes, which together stalled launch
    // for seconds on a folder of a few thousand images (#4).
    //
    // This is the app's one owner of background work and of abandoning it. The rules the screen
    // depends on:
    //
    //   * A load is abandonable (INV-X-13). Only the most recent may write the pool; a load whose
    //     folder has been superseded is discarded, including everything it decoded. Abandonment is a
    //     generation counter rather than a CancellationTokenSource because nothing on this path is a
    //     cancellation-aware API — every check is an ordinary `if` — so a token would buy a disposal
    //     puzzle and nothing else.
    //   * The walk and the decodes never touch a view, the Activity, or Settings. WalkAndDecode is
    //     static so that is a compiler guarantee rather than a review comment; what keeps the
    //     Activity unreachable is that this loader never holds one. That is also why it takes the
    //     *application* ContentResolver — an Activity's resolver reaches back to the Activity
    //     through its ContextImpl, and a walk in flight would pin a destroyed screen's view tree.
    //   * An abandoned walk yields a *truncated* pool, which is safe only because LoadAsync discards
    //     it before returning. Hand it back before re-checking the generation and the artist draws
    //     from a fraction of their folder with no error anywhere.
    //
    // Everything here is called on the main thread except the body of the one Task.Run.
    sealed class LibraryLoader(ContentResolver resolver)
    {
        const string LogTag = "FigureDrawing";

        // Which load is current. The arithmetic is Core's (LoadGeneration) so that "a superseded
        // load is never current again" is covered by a test that runs it, rather than by a contract
        // test reading this file — source text cannot tell a working comparison from an inverted one.
        readonly LoadGeneration generation = new();

        // Abandons whatever is in flight. Called from OnStop, OnDestroy, and ResetLibrary — the last
        // because a screen that has just told the artist a folder could not be opened must not then
        // be repopulated by the load it was waiting on. Never OnPause: a briefly backgrounded app
        // must not come back to an empty library.
        public void Abandon() => generation.Abandon();

        // Whether the load holding this ticket is still the one allowed to write the screen.
        bool IsCurrent(int ticket) => generation.IsCurrent(ticket);

        // Walks the tree and decodes up to maxThumbnails previews, off the UI thread. Returns null
        // when the load was superseded or abandoned, having already recycled anything it decoded.
        public async Task<LibraryLoad?> LoadAsync(Android.Net.Uri treeUri, int maxThumbnails,
            int thumbnailDimension, int maxThumbnailDimension)
        {
            // Taken first, so a load that never reaches the walk still supersedes the one in flight.
            var mine = generation.Take();

            // Owned by the caller of Task.Run, so a throw out of the worker still leaves whatever was
            // decoded reachable for the catch below to free.
            var decoded = new List<Bitmap?>();

            try
            {
                // Inside the try, so that even this pre-walk step cannot throw past the superseded
                // check below — the comment that used to sit here claimed every exit was guarded
                // while this one was not, which is how a stale failure gets back out.
                //
                // A tree with no document id is a folder this app cannot open, which is a *failure*
                // and not an empty folder: returning an empty library here would caption it "No
                // images found in that folder", telling the artist their folder is empty when it
                // could not be read. Thrown, it reaches LoadFolderAsync's catch and the folder-error
                // caption — and, being inside the try, it is still swallowed if this load is stale.
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

                // A load nobody is waiting for reports nothing — including its failures (INV-X-13).
                // The screen it would report to belongs to a newer load, or is gone: letting a stale
                // failure through would wipe the live load's pool, caption a folder that is fine as
                // unopenable, and abandon the load that was about to succeed.
                if (!IsCurrent(mine))
                {
                    Log.Warn(LogTag, $"Abandoned load failed after being superseded: {error.Message}");
                    return null;
                }

                throw;
            }
        }

        // The background half. Static, and every dependency arrives as a parameter: nothing here can
        // reach a view, the Activity, or Settings.
        //
        // isCurrent is checked per query and per image rather than once up front — rapid stop/start
        // churn would otherwise leave several full walks running at once, each blocked on binder I/O
        // against a provider that may be network-backed.
        static ReferenceLibrary WalkAndDecode(
            ContentResolver resolver, Android.Net.Uri treeUri, string rootDocumentId,
            List<Bitmap?> decoded, int maxThumbnails, int thumbnailDimension, int maxThumbnailDimension,
            Func<bool> isCurrent)
        {
            var tree = new ContentResolverDocumentTree(resolver, treeUri, isCurrent);

            // The mapper runs for every image the walk finds, not just the ones previewed, so its Uri
            // is disposed rather than left for a finalizer pass: a few thousand undisposed peers is
            // a few thousand live JNI global refs out of the process-wide 51,200 (ARCHITECTURE §8).
            var library = new ReferenceLibrary(
                tree,
                rootDocumentId,
                treeUri.LastPathSegment,
                documentId =>
                {
                    using var documentUri = DocumentsContract.BuildDocumentUriUsingTree(treeUri, documentId);
                    return documentUri?.ToString();
                });

            // Every image found is in the pool regardless of whether its preview decodes; the session
            // handles any that turn out unreadable. The cap bounds decode ATTEMPTS, not successes — a
            // folder of undecodable files must not cost one provider round trip per entry.
            foreach (var id in library.Pool.Take(maxThumbnails))
            {
                if (!isCurrent())
                {
                    // Freed here, on the thread that decoded them, rather than left for the
                    // continuation: the load that superseded this one is decoding at the same time,
                    // and holding both sets until the looper drains doubles the peak this cap exists
                    // to bound. LoadAsync discards again on the way out, which is a no-op by then.
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

        // One preview, or null when it cannot be read. A single unreadable/oversized image must not
        // sink the whole folder, so a failure here is logged and skipped rather than thrown.
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

        // Frees previews that never reached the grid. A JNI global ref keeps a Bitmap alive until a
        // managed GC plus finalizer pass, which is far too late (docs/ARCHITECTURE.md §8) — this is
        // the unattached counterpart of MainActivity.ClearThumbnails.
        public static void DiscardThumbnails(List<Bitmap?> bitmaps)
        {
            foreach (var bitmap in bitmaps)
            {
                // A cleared slot is one a view has taken ownership of — freeing it here would
                // recycle pixels the grid is currently showing.
                if (bitmap is null)
                    continue;

                if (!bitmap.IsRecycled)
                    bitmap.Recycle();

                bitmap.Dispose();
            }

            bitmaps.Clear();
        }

        // Adapts a Storage Access Framework tree (DocumentsContract + ContentResolver) to the
        // IDocumentTree abstraction the pure enumerator walks.
        sealed class ContentResolverDocumentTree(
            ContentResolver resolver, Android.Net.Uri treeUri, Func<bool> isCurrent) : IDocumentTree
        {
            // A failed query yields nothing rather than throwing: the provider may be gone, the
            // volume unmounted, or the grant revoked between the permission check and the walk, and
            // the domain treats "no children" as an ordinary answer (INV-TREE-4, INV-GRP-5).
            //
            // An abandoned load stops asking, and stops the same way. That is why the abandonment
            // check lives here rather than on IDocumentTree: a cancelled query is indistinguishable
            // from a revoked one, a rule the domain already models, so no threading concept crosses
            // into Core. The truncated pool it leaves is discarded by LoadAsync, never shown.
            public IEnumerable<DocumentEntry> GetChildren(string parentDocumentId)
            {
                var entries = new List<DocumentEntry>();

                if (!isCurrent())
                    return entries;

                try
                {
                    // Disposed rather than left to a finalizer: one peer per folder queried, and the
                    // walk now re-runs on every return to the screen, against the process-wide JNI
                    // global-ref ceiling (ARCHITECTURE.md §8). Exhausting that table aborts the
                    // process rather than failing gracefully.
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

                    // Close releases the native window; the using above disposes the managed peer.
                    try
                    {
                        while (cursor.MoveToNext())
                        {
                            // Per row, not just per query. This app's usual shape is one flat folder
                            // of thousands of images, and for that "per query" is the same as "once
                            // up front": an abandoned load would drain the whole cursor, refilling
                            // binder windows and mapping every id, before it could stop.
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
