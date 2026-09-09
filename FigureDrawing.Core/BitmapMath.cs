namespace FigureDrawing.Core;

public static class BitmapMath
{
    public static int CalculateInSampleSize(int srcWidth, int srcHeight, int reqWidth, int reqHeight)
    {
        if (srcWidth <= 0 || srcHeight <= 0 || reqWidth <= 0 || reqHeight <= 0)
            return 1;

        var sampleSize = 1;
        while (srcHeight / (sampleSize * 2) >= reqHeight && srcWidth / (sampleSize * 2) >= reqWidth)
            sampleSize *= 2;

        return sampleSize;
    }

    public static int CalculateBoundedSampleSize(int srcWidth, int srcHeight, int maxDimension)
    {
        if (srcWidth <= 0 || srcHeight <= 0 || maxDimension <= 0)
            return 1;

        var sampleSize = 1;
        while (Math.Max(srcWidth, srcHeight) / (sampleSize * 2) >= maxDimension)
            sampleSize *= 2;

        return sampleSize;
    }

    // Max, not sum: BitmapFactory honours only powers of two, and the max of two still is one.
    public static int CalculateCropSampleSize(int srcWidth, int srcHeight, int request, int maxDimension) =>
        Math.Max(CalculateInSampleSize(srcWidth, srcHeight, request, request),
                 CalculateBoundedSampleSize(srcWidth, srcHeight, maxDimension));
}
