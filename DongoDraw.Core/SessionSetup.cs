namespace DongoDraw.Core;

public readonly record struct SessionConfig(int SecondsPerImage, int ImageCount, int BreakSeconds = 0);

public static class SessionSetup
{
    public const int DefaultSecondsPerImage = 30;
    public const int DefaultImageCount = 20;
    public const int DefaultBreakSeconds = 0;

    public static readonly IReadOnlyList<int> SecondsPresets = new[] { 30, 60, 120, 300 };
    public static readonly IReadOnlyList<int> BreakPresets = new[] { 0, 5, 15, 60 };

    public static bool IsValidSeconds(int seconds) => seconds > 0;
    public static bool IsValidCount(int count) => count > 0;

    public static int? ParsePositive(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;

        return int.TryParse(raw.Trim(), out var value) && value > 0 ? value : null;
    }

    // 5 rather than 1 so a second run of the same session redraws different images (§3.1).
    public const int HandoffPerImage = 5;
    public const int MinHandoff = 50;

    public static int HandoffBound(int imageCount, int maxIds)
    {
        var ceiling = Math.Max(0, maxIds);

        // In long: the count is unbounded above (INV-SET-1) and an overflow wraps to an empty pool.
        var wanted = Math.Max((long)Math.Max(0, imageCount) * HandoffPerImage, MinHandoff);

        return (int)Math.Min(wanted, ceiling);
    }

    public static int EstimateSeconds(SessionConfig config)
    {
        var seconds = Math.Max(0, config.SecondsPerImage);
        var count = Math.Max(0, config.ImageCount);
        var breaks = Math.Max(0, config.BreakSeconds);

        return seconds * count + breaks * Math.Max(0, count - 1);
    }
}
