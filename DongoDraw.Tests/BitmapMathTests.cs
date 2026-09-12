using DongoDraw.Core;

namespace DongoDraw.Tests;

// Sample-size math that bounds decoded image memory (the fix for the folder-of-photos crash).
public class BitmapMathTests
{
    [Theory]
    // Already small enough -> no downsample.
    [InlineData(800, 600, 1080, 1080, 1)]
    [InlineData(1080, 1080, 1080, 1080, 1)]
    // 4000x4000 photo into a 1080 box -> /2 (2000) still >=1080, /4 (1000) < 1080 -> stop at 2.
    [InlineData(4000, 4000, 1080, 1080, 2)]
    // 8000x6000 -> 2 (4000/3000) ok, 4 (2000/1500) ok, 8 (1000/750) < 1080 -> 4.
    [InlineData(8000, 6000, 1080, 1080, 4)]
    // Very large -> larger factor.
    [InlineData(20000, 20000, 1080, 1080, 16)]
    public void CalculateInSampleSize_PicksBoundedPowerOfTwo(int sw, int sh, int rw, int rh, int expected) =>
        Assert.Equal(expected, BitmapMath.CalculateInSampleSize(sw, sh, rw, rh));

    [Theory]
    [InlineData(0, 100, 1080, 1080)]
    [InlineData(100, 0, 1080, 1080)]
    [InlineData(100, 100, 0, 1080)]
    [InlineData(-1, -1, -1, -1)]
    public void CalculateInSampleSize_NonPositiveInput_ReturnsOne(int sw, int sh, int rw, int rh) =>
        Assert.Equal(1, BitmapMath.CalculateInSampleSize(sw, sh, rw, rh));

    [Fact]
    public void CalculateInSampleSize_ResultIsAlwaysPowerOfTwo()
    {
        for (var dim = 100; dim <= 20000; dim += 137)
        {
            var s = BitmapMath.CalculateInSampleSize(dim, dim, 1080, 1080);
            Assert.True((s & (s - 1)) == 0, $"sample size {s} for {dim} is not a power of two");
        }
    }

    [Theory]
    // Already inside the ceiling -> no downsample.
    [InlineData(800, 600, 1080, 1)]
    [InlineData(1080, 1080, 1080, 1)]
    // Square photo: 4000/2 = 2000 >= 1080, /4 = 1000 < 1080 -> stop at 2.
    [InlineData(4000, 4000, 1080, 2)]
    // The panorama §8 names: the LONG side is what is bounded, so 12000/8 = 1500 >= 1080,
    // /16 = 750 < 1080 -> 8, decoding 1500x112 instead of 12000x900.
    [InlineData(12000, 900, 1080, 8)]
    // Aspect extreme the other way round — the rule reads the max, not the width.
    [InlineData(900, 12000, 1080, 8)]
    public void CalculateBoundedSampleSize_BoundsTheLongSide(int sw, int sh, int max, int expected) =>
        Assert.Equal(expected, BitmapMath.CalculateBoundedSampleSize(sw, sh, max));

    [Theory]
    [InlineData(0, 100, 1080)]
    [InlineData(100, 0, 1080)]
    [InlineData(100, 100, 0)]
    [InlineData(-1, -1, -1)]
    public void CalculateBoundedSampleSize_NonPositiveInput_ReturnsOne(int sw, int sh, int max) =>
        Assert.Equal(1, BitmapMath.CalculateBoundedSampleSize(sw, sh, max));

    // The decoded long side always lands under 2x the ceiling — the "within 2x" the docs quote.
    [Fact]
    public void CalculateBoundedSampleSize_LeavesTheLongSideUnderTwiceTheCeiling()
    {
        for (var dim = 1080; dim <= 20000; dim += 131)
        {
            var sampled = dim / BitmapMath.CalculateBoundedSampleSize(dim, dim / 3, 1080);
            Assert.True(sampled < 2160, $"{dim} sampled to {sampled}, past 2x the 1080 ceiling");
        }
    }

    [Theory]
    // Pose path: floor and ceiling are the same value, so it matches the single-rule result.
    [InlineData(4000, 4000, 1080, 1080, 2)]
    [InlineData(1080, 1080, 1080, 1080, 1)]
    // Thumbnail path: the crop floor wins on an ordinary photo (a 360 px tile wants 504x378).
    [InlineData(4032, 3024, 360, 720, 8)]
    // ...and the ceiling wins on a panorama, taking the short side below the request rather than
    // decoding the long one at full width.
    [InlineData(12000, 900, 360, 1080, 8)]
    [InlineData(2000, 700, 360, 720, 2)]
    public void CalculateCropSampleSize_TakesWhicheverRuleBindsHarder(
        int sw, int sh, int request, int max, int expected) =>
        Assert.Equal(expected, BitmapMath.CalculateCropSampleSize(sw, sh, request, max));

    // Math.Max of two powers of two is a power of two — the BitmapFactory contract.
    [Fact]
    public void CalculateCropSampleSize_ResultIsAlwaysPowerOfTwo()
    {
        for (var dim = 100; dim <= 20000; dim += 137)
        {
            var s = BitmapMath.CalculateCropSampleSize(dim, dim / 4, 360, 720);
            Assert.True((s & (s - 1)) == 0, $"sample size {s} for {dim} is not a power of two");
        }
    }

    // A missing ceiling degrades to the crop floor rather than to "decode everything".
    [Fact]
    public void CalculateCropSampleSize_NonPositiveCeiling_FallsBackToTheCropFloor() =>
        Assert.Equal(BitmapMath.CalculateInSampleSize(4000, 3000, 360, 360),
                     BitmapMath.CalculateCropSampleSize(4000, 3000, 360, 0));
}
