using Android.Content;
using Android.Graphics;
using FigureDrawing.Core;

namespace FigureDrawing
{
    // Shared reference-image decoding used by both the folder preview (MainActivity) and the session
    // player (SessionActivity). Real photos are far larger than the screen, so it down-samples: the
    // first pass reads only the bounds, the second decodes at the computed sample size, keeping a
    // folder of full-resolution images within memory. Returns null when the uri can't be decoded.
    //
    // Two bounds, not one: requestDimension is the quality floor for the short side (a tile is
    // center-cropped, so decoding below it would upscale), maxDimension is the memory ceiling for
    // the long side and holds whatever the aspect ratio is — a 12000x900 panorama is sampled down
    // rather than decoded at full width.
    internal static class ImageDecoding
    {
        // cancelled: checked between the two passes, which is where the multi-megabyte allocation
        // starts. A decode already past that point runs to completion — the platform gives no way to
        // stop it — so this bounds what an abandoned prefetch costs, it does not abort one.
        public static Bitmap? DecodeSampledBitmap(
            ContentResolver resolver,
            Android.Net.Uri uri,
            int requestDimension,
            int maxDimension,
            CancellationToken cancelled = default)
        {
            using var bounds = new BitmapFactory.Options { InJustDecodeBounds = true };
            using (var stream = resolver.OpenInputStream(uri))
                BitmapFactory.DecodeStream(stream, null, bounds);

            if (cancelled.IsCancellationRequested)
                return null;

            using var options = new BitmapFactory.Options
            {
                InSampleSize = BitmapMath.CalculateCropSampleSize(
                    bounds.OutWidth, bounds.OutHeight, requestDimension, maxDimension),
            };

            using var decodeStream = resolver.OpenInputStream(uri);
            return BitmapFactory.DecodeStream(decodeStream, null, options);
        }
    }
}
