using Android.Content;
using Android.Graphics;
using DongoDraw.Core;

namespace DongoDraw
{
    internal static class ImageDecoding
    {
        // Between the passes, before the big allocation: a decode past here cannot be aborted.
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
