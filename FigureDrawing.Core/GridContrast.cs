namespace FigureDrawing.Core;

public readonly record struct GridPalette(int LightLine, int LightCasing, int DarkLine, int DarkCasing);

public readonly record struct GridLineStyle(int LineArgb, int CasingArgb);

public readonly record struct GridStyles(
    GridLineStyle VerticalLeft,
    GridLineStyle VerticalRight,
    GridLineStyle HorizontalTop,
    GridLineStyle HorizontalBottom);

public static class GridContrast
{
    // 32x32 = 1024 ints (~4 KB): free to sample, still several distinct columns per band.
    public const int SampleGrid = 32;

    public const double LightThreshold = 0.5;

    // 5% each way: the line's own visual weight plus the eye's immediate surround.
    public const double BandHalfWidth = 0.05;

    const double FirstThird = 1.0 / 3.0;
    const double SecondThird = 2.0 / 3.0;

    // Alpha ignored: a decoded pose is opaque and the letterbox falls back instead.
    public static double Luminance(int argb)
    {
        var r = ((argb >> 16) & 0xFF) / 255.0;
        var g = ((argb >> 8) & 0xFF) / 255.0;
        var b = (argb & 0xFF) / 255.0;
        return (0.2126 * r) + (0.7152 * g) + (0.0722 * b);
    }

    // Casing is the opposite tone: it carries the line across a patch fighting the average.
    public static GridLineStyle ForLuminance(double luminance, GridPalette palette) =>
        luminance > LightThreshold
            ? new GridLineStyle(palette.DarkLine, palette.LightCasing)
            : new GridLineStyle(palette.LightLine, palette.DarkCasing);

    public static GridStyles LineStyles(
        ReadOnlySpan<int> samples,
        int grid,
        int stageWidth,
        int stageHeight,
        int imageWidth,
        int imageHeight,
        double zoom,
        bool flip,
        GridPalette palette)
    {
        var fallback = ForLuminance(0.0, palette);
        var allFallback = new GridStyles(fallback, fallback, fallback, fallback);

        if (grid <= 0 || samples.Length < grid * grid)
            return allFallback;

        if (stageWidth <= 0 || stageHeight <= 0 || imageWidth <= 0 || imageHeight <= 0)
            return allFallback;

        if (zoom <= 0 || double.IsNaN(zoom) || double.IsInfinity(zoom))
            return allFallback;

        var fit = Math.Min((double)stageWidth / imageWidth, (double)stageHeight / imageHeight);
        var drawnWidth = imageWidth * fit * zoom;
        var drawnHeight = imageHeight * fit * zoom;
        var left = (stageWidth - drawnWidth) / 2.0;
        var top = (stageHeight - drawnHeight) / 2.0;

        // On-screen part only, or a guide takes its colour from pixels nobody can see.
        var visibleColumns = VisibleCells(left, drawnWidth, stageWidth, grid);
        var visibleRows = VisibleCells(top, drawnHeight, stageHeight, grid);

        // Flip is a negative horizontal scale, so only the vertical pair reads mirrored.
        var firstColumn = BandCells(Normalize(FirstThird * stageWidth, left, drawnWidth, flip), grid);
        var secondColumn = BandCells(Normalize(SecondThird * stageWidth, left, drawnWidth, flip), grid);
        var firstRow = BandCells(Normalize(FirstThird * stageHeight, top, drawnHeight, mirror: false), grid);
        var secondRow = BandCells(Normalize(SecondThird * stageHeight, top, drawnHeight, mirror: false), grid);

        return new GridStyles(
            Resolve(samples, grid, firstColumn, visibleRows, palette, fallback),
            Resolve(samples, grid, secondColumn, visibleRows, palette, fallback),
            Resolve(samples, grid, visibleColumns, firstRow, palette, fallback),
            Resolve(samples, grid, visibleColumns, secondRow, palette, fallback));
    }

    static double Normalize(double stagePosition, double origin, double size, bool mirror)
    {
        var position = (stagePosition - origin) / size;
        return mirror ? 1.0 - position : position;
    }

    static GridLineStyle Resolve(
        ReadOnlySpan<int> samples,
        int grid,
        CellRange columns,
        CellRange rows,
        GridPalette palette,
        GridLineStyle fallback) =>
        columns.IsValid && rows.IsValid
            ? ForLuminance(MeanLuminance(samples, grid, columns, rows), palette)
            : fallback;

    static CellRange BandCells(double position, int grid)
    {
        if (position < 0.0 || position > 1.0 || double.IsNaN(position))
            return CellRange.Invalid;

        var lo = ToCell(position - BandHalfWidth, grid);
        var hi = ToCell(position + BandHalfWidth, grid);

        // Narrowed at an edge, never below the cell the guide itself sits in.
        var centre = ToCell(position, grid);
        return new CellRange(Math.Min(lo, centre), Math.Max(hi, centre));
    }

    static CellRange VisibleCells(double origin, double size, int stageSize, int grid)
    {
        var lo = (0.0 - origin) / size;
        var hi = (stageSize - origin) / size;

        if (hi <= 0.0 || lo >= 1.0)
            return CellRange.Invalid;

        return new CellRange(ToCell(lo, grid), ToCell(hi, grid));
    }

    static int ToCell(double position, int grid) =>
        Math.Clamp((int)(position * grid), 0, grid - 1);

    static double MeanLuminance(ReadOnlySpan<int> samples, int grid, CellRange columns, CellRange rows)
    {
        var total = 0.0;
        var count = 0;

        for (var row = rows.Low; row <= rows.High; row++)
        {
            var offset = row * grid;
            for (var column = columns.Low; column <= columns.High; column++)
            {
                total += Luminance(samples[offset + column]);
                count++;
            }
        }

        return count == 0 ? 0.0 : total / count;
    }

    readonly record struct CellRange(int Low, int High)
    {
        public static CellRange Invalid => new(1, 0);

        public bool IsValid => Low <= High;
    }
}
