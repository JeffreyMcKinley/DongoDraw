using Android.Graphics;

namespace FigureDrawing
{
    // The screen owns the bitmap and frees it (ARCHITECTURE.md §8).
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

        public int[]? GuideSamples { get; }

        // Captured at decode time: reading Bitmap.Width once recycled is a use-after-free.
        public int Width { get; }

        public int Height { get; }
    }
}
