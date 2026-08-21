using Android.Graphics;

namespace FigureDrawing
{
    // One decoded reference image, together with everything the player screen derives from its
    // pixels. This is the `TImage` the session aggregate is generic over — Core never looks inside
    // it, which is what keeps Bitmap out of the domain (docs/ARCHITECTURE.md §4).
    //
    // It exists so that every per-pose pixel operation happens on the thread that decoded the image
    // rather than on the repaint callback: the guide samples below are a scaled copy plus a
    // GetPixels, and computing them in Render put both back on the UI thread at exactly the moment
    // the pose changes.
    //
    // The screen owns the bitmap and frees it (docs/ARCHITECTURE.md §8); this type holds no
    // disposable state of its own beyond it.
    internal sealed class PoseImage
    {
        public PoseImage(Bitmap bitmap, int[]? guideSamples, int width, int height)
        {
            Bitmap = bitmap;
            GuideSamples = guideSamples;
            Width = width;
            Height = height;
        }

        public Bitmap Bitmap { get; }

        // The pose reduced to a GridContrast.SampleGrid block, or null when it could not be sampled
        // — the guides then fall back to the light style rather than the screen failing.
        public int[]? GuideSamples { get; }

        // The decoded bitmap's dimensions, captured at decode time so the guides can be re-resolved
        // after the bitmap itself has been recycled without touching it again.
        public int Width { get; }

        public int Height { get; }
    }
}
