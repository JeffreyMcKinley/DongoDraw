using FigureDrawing.Core;

namespace FigureDrawing.Tests;

// Contrast math for the rule-of-thirds overlay: which tone each guide takes from the strip of
// image it crosses, once the pose has been fitCenter'd, zoomed and possibly mirrored.
public class GridContrastTests
{
    // Stand-ins for the four colors.xml tokens. Distinct values so an assertion says which one won.
    const int LightLine = 0x59E9E9ED;
    const int LightCasing = 0x40E9E9ED;
    const int DarkLine = 0x59000000;
    const int DarkCasing = 0x40000000;

    static readonly GridPalette Palette = new(LightLine, LightCasing, DarkLine, DarkCasing);

    static readonly GridLineStyle Light = new(LightLine, DarkCasing);
    static readonly GridLineStyle Dark = new(DarkLine, LightCasing);

    const int Grid = GridContrast.SampleGrid;

    const int White = unchecked((int)0xFFFFFFFF);
    const int Black = unchecked((int)0xFF000000);

    [Theory]
    // The sRGB coefficients, pinned one channel at a time.
    [InlineData(unchecked((int)0xFF000000), 0.0)]
    [InlineData(unchecked((int)0xFFFFFFFF), 1.0)]
    [InlineData(unchecked((int)0xFFFF0000), 0.2126)]
    [InlineData(unchecked((int)0xFF00FF00), 0.7152)]
    [InlineData(unchecked((int)0xFF0000FF), 0.0722)]
    public void Luminance_UsesTheSrgbCoefficients(int argb, double expected) =>
        Assert.Equal(expected, GridContrast.Luminance(argb), 4);

    // Alpha is not a channel here — a decoded pose is opaque, and the letterbox is handled by
    // falling back rather than by averaging transparent pixels.
    [Fact]
    public void Luminance_IgnoresAlpha() =>
        Assert.Equal(GridContrast.Luminance(unchecked((int)0xFFFFFFFF)),
                     GridContrast.Luminance(0x00FFFFFF));

    [Theory]
    [InlineData(0.0)]
    [InlineData(0.49)]
    // Exactly at the threshold stays light: the test is "brighter than", not "at least".
    [InlineData(0.5)]
    public void ForLuminance_DarkRegion_TakesTheLightLine(double luminance) =>
        Assert.Equal(Light, GridContrast.ForLuminance(luminance, Palette));

    [Theory]
    [InlineData(0.51)]
    [InlineData(1.0)]
    public void ForLuminance_LightRegion_TakesTheDarkLine(double luminance) =>
        Assert.Equal(Dark, GridContrast.ForLuminance(luminance, Palette));

    // The casing exists to carry the line over a local patch that fights the region's average, so
    // it is always the opposite tone of the core. A black casing behind a black line does nothing.
    [Fact]
    public void ForLuminance_CasingIsAlwaysTheOppositeToneOfTheCore()
    {
        for (var luminance = 0.0; luminance <= 1.0; luminance += 0.05)
        {
            var style = GridContrast.ForLuminance(luminance, Palette);
            var lineIsLight = style.LineArgb == LightLine;
            Assert.Equal(lineIsLight ? DarkCasing : LightCasing, style.CasingArgb);
        }
    }

    // The case that motivated sampling per line rather than averaging the whole pose: one
    // compromise colour would be wrong on both halves.
    [Fact]
    public void LineStyles_HalfLightHalfDarkImage_GivesTheTwoVerticalsDifferentTones()
    {
        var samples = ColumnSplit(White, Black);

        var styles = GridContrast.LineStyles(
            samples, Grid, 900, 900, 900, 900, zoom: 1.0, flip: false, Palette);

        // Left guide crosses the white half, right guide the black half.
        Assert.Equal(Dark, styles.VerticalLeft);
        Assert.Equal(Light, styles.VerticalRight);
    }

    // A square pose in a wide stage is pillarboxed: both verticals sit on the bar, not on the
    // image, and fall back to the light style that reads over @color/stage.
    [Fact]
    public void LineStyles_PillarboxedPose_FallsBackOnTheVerticals()
    {
        var samples = Uniform(White);

        var styles = GridContrast.LineStyles(
            samples, Grid, 1200, 300, 1000, 1000, zoom: 1.0, flip: false, Palette);

        Assert.Equal(Light, styles.VerticalLeft);
        Assert.Equal(Light, styles.VerticalRight);

        // The horizontals do cross the pose, and it is white.
        Assert.Equal(Dark, styles.HorizontalTop);
        Assert.Equal(Dark, styles.HorizontalBottom);
    }

    // The transpose: a square pose in a tall stage is letterboxed instead.
    [Fact]
    public void LineStyles_LetterboxedPose_FallsBackOnTheHorizontals()
    {
        var samples = Uniform(White);

        var styles = GridContrast.LineStyles(
            samples, Grid, 300, 1200, 1000, 1000, zoom: 1.0, flip: false, Palette);

        Assert.Equal(Light, styles.HorizontalTop);
        Assert.Equal(Light, styles.HorizontalBottom);

        Assert.Equal(Dark, styles.VerticalLeft);
        Assert.Equal(Dark, styles.VerticalRight);
    }

    // Zooming grows the drawn rect past the stage edges, so the stage thirds map to positions
    // closer to the middle of the image. A pose that is only light down its centre proves the
    // bands actually moved: at 1:1 both guides read the dark surround, zoomed in they read the
    // light middle.
    [Fact]
    public void LineStyles_Zoom_MovesTheSampledBandsTowardTheCentre()
    {
        var samples = ColumnBand(12, 19, White, Black);

        var fit = GridContrast.LineStyles(
            samples, Grid, 900, 900, 900, 900, zoom: 1.0, flip: false, Palette);

        Assert.Equal(Light, fit.VerticalLeft);
        Assert.Equal(Light, fit.VerticalRight);

        var zoomed = GridContrast.LineStyles(
            samples, Grid, 900, 900, 900, 900, zoom: 2.5, flip: false, Palette);

        Assert.Equal(Dark, zoomed.VerticalLeft);
        Assert.Equal(Dark, zoomed.VerticalRight);
    }

    // Flip is a negative horizontal scale, so each vertical guide reads the mirrored column — and
    // the horizontals are untouched, because the app never mirrors vertically.
    [Fact]
    public void LineStyles_Flip_SwapsWhatTheVerticalsRead()
    {
        var samples = ColumnSplit(White, Black);

        var plain = GridContrast.LineStyles(
            samples, Grid, 900, 900, 900, 900, zoom: 1.0, flip: false, Palette);
        var flipped = GridContrast.LineStyles(
            samples, Grid, 900, 900, 900, 900, zoom: 1.0, flip: true, Palette);

        Assert.Equal(plain.VerticalRight, flipped.VerticalLeft);
        Assert.Equal(plain.VerticalLeft, flipped.VerticalRight);

        Assert.Equal(plain.HorizontalTop, flipped.HorizontalTop);
        Assert.Equal(plain.HorizontalBottom, flipped.HorizontalBottom);
    }

    // A guide landing exactly on an image edge narrows its band rather than reading out of bounds.
    // This stage puts the verticals on u = 0 and u = 1 precisely.
    [Fact]
    public void LineStyles_GuideOnTheImageEdge_ClampsTheBandInsteadOfThrowing()
    {
        var samples = Uniform(White);

        var styles = GridContrast.LineStyles(
            samples, Grid, 900, 300, 1000, 1000, zoom: 1.0, flip: false, Palette);

        Assert.Equal(Dark, styles.VerticalLeft);
        Assert.Equal(Dark, styles.VerticalRight);
    }

    public static TheoryData<int, int, int, int, int, double> Degenerate => new()
    {
        // grid, stageW, stageH, imageW, imageH, zoom
        { 0, 900, 900, 900, 900, 1.0 },      // no grid
        { -1, 900, 900, 900, 900, 1.0 },
        { Grid, 0, 900, 900, 900, 1.0 },     // stage not laid out yet
        { Grid, 900, 0, 900, 900, 1.0 },
        { Grid, 900, 900, 0, 900, 1.0 },     // nonsense image bounds
        { Grid, 900, 900, 900, 0, 1.0 },
        { Grid, -10, -10, -10, -10, 1.0 },
        { Grid, 900, 900, 900, 900, 0.0 },   // zoom that would divide by zero
        { Grid, 900, 900, 900, 900, -1.0 },
        { Grid, 900, 900, 900, 900, double.NaN },
        { Grid, 900, 900, 900, 900, double.PositiveInfinity },
    };

    // This runs on the render path, so every degenerate input returns four light styles rather
    // than throwing. A bad frame must never sink the screen.
    [Theory]
    [MemberData(nameof(Degenerate))]
    public void LineStyles_DegenerateInput_FallsBackWithoutThrowing(
        int grid, int stageWidth, int stageHeight, int imageWidth, int imageHeight, double zoom)
    {
        var samples = Uniform(White);

        var styles = GridContrast.LineStyles(
            samples, grid, stageWidth, stageHeight, imageWidth, imageHeight, zoom, flip: false, Palette);

        AssertAllLight(styles);
    }

    // A span too short for the declared grid is the "sampling failed" path the screen takes when
    // the bitmap could not be read.
    [Fact]
    public void LineStyles_ShortSampleSpan_FallsBack()
    {
        var styles = GridContrast.LineStyles(
            new int[(Grid * Grid) - 1], Grid, 900, 900, 900, 900, zoom: 1.0, flip: false, Palette);

        AssertAllLight(styles);
    }

    [Fact]
    public void LineStyles_NoSamples_FallsBack() =>
        AssertAllLight(GridContrast.LineStyles(
            ReadOnlySpan<int>.Empty, Grid, 900, 900, 900, 900, zoom: 1.0, flip: false, Palette));

    static void AssertAllLight(GridStyles styles)
    {
        Assert.Equal(Light, styles.VerticalLeft);
        Assert.Equal(Light, styles.VerticalRight);
        Assert.Equal(Light, styles.HorizontalTop);
        Assert.Equal(Light, styles.HorizontalBottom);
    }

    static int[] Uniform(int argb)
    {
        var samples = new int[Grid * Grid];
        Array.Fill(samples, argb);
        return samples;
    }

    // Left half one colour, right half the other — a vertical split, so every row reads the same.
    static int[] ColumnSplit(int left, int right) =>
        Build(column => column < Grid / 2 ? left : right);

    // A vertical band of `inside` between the given columns (inclusive), `outside` elsewhere.
    static int[] ColumnBand(int from, int to, int inside, int outside) =>
        Build(column => column >= from && column <= to ? inside : outside);

    static int[] Build(Func<int, int> byColumn)
    {
        var samples = new int[Grid * Grid];
        for (var row = 0; row < Grid; row++)
        for (var column = 0; column < Grid; column++)
            samples[(row * Grid) + column] = byColumn(column);

        return samples;
    }
}
