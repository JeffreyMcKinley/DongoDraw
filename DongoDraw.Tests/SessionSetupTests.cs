using DongoDraw.Core;

namespace DongoDraw.Tests;

// FD-002 session-setup: the pure parsing, validity and pacing helpers behind the setup screen. The
// evaluated state of that screen is a draft session and is tested in DrawingSessionSetupTests.
public class SessionSetupTests
{
    [Theory]
    [InlineData("30", 30)]
    [InlineData("1", 1)]
    [InlineData("  45  ", 45)]     // surrounding whitespace tolerated
    [InlineData("0", null)]        // not > 0
    [InlineData("-5", null)]
    [InlineData("", null)]
    [InlineData("   ", null)]
    [InlineData(null, null)]
    [InlineData("abc", null)]
    [InlineData("3.5", null)]      // not an integer
    public void ParsePositive_ParsesOnlyPositiveIntegers(string? raw, int? expected) =>
        Assert.Equal(expected, SessionSetup.ParsePositive(raw));

    [Theory]
    [InlineData(1, true)]
    [InlineData(30, true)]
    [InlineData(0, false)]
    [InlineData(-1, false)]
    public void IsValidSeconds_RequiresPositive(int seconds, bool expected) =>
        Assert.Equal(expected, SessionSetup.IsValidSeconds(seconds));

    [Theory]
    [InlineData(1, true)]
    [InlineData(20, true)]
    [InlineData(0, false)]
    [InlineData(-1, false)]
    public void IsValidCount_RequiresPositive(int count, bool expected) =>
        Assert.Equal(expected, SessionSetup.IsValidCount(count));

    [Fact]
    public void Defaults_ArePositive()
    {
        Assert.True(SessionSetup.IsValidSeconds(SessionSetup.DefaultSecondsPerImage));
        Assert.True(SessionSetup.IsValidCount(SessionSetup.DefaultImageCount));
    }

    [Theory]
    [InlineData(30, 12, 0, 360)]        // 12 * 30s, no breaks
    [InlineData(60, 10, 15, 735)]       // 10 * 60s + 9 breaks of 15s
    [InlineData(30, 1, 60, 30)]         // a single pose has no break after it
    [InlineData(30, 0, 15, 0)]          // nothing to draw, nothing to estimate
    public void EstimateSeconds_CountsBreaksBetweenPosesOnly(
        int seconds, int count, int breakSeconds, int expected) =>
        Assert.Equal(expected, SessionSetup.EstimateSeconds(new SessionConfig(seconds, count, breakSeconds)));

    // The setup screen renders one chip per preset, so the presets must stay usable inputs.
    [Fact]
    public void SecondsPresets_AreAllValidDurations()
    {
        Assert.NotEmpty(SessionSetup.SecondsPresets);
        Assert.All(SessionSetup.SecondsPresets, s => Assert.True(SessionSetup.IsValidSeconds(s)));
        Assert.Contains(SessionSetup.DefaultSecondsPerImage, SessionSetup.SecondsPresets);
    }

    [Fact]
    public void BreakPresets_StartAtNone_AndAreNeverNegative()
    {
        Assert.Equal(0, SessionSetup.BreakPresets[0]);
        Assert.All(SessionSetup.BreakPresets, b => Assert.True(b >= 0));
    }

    // A session draws from what it was handed and cannot ask the library for more, so the bound is a
    // function of how long the session is — not a flat number sized only by what the transport will
    // carry.
    [Theory]
    [InlineData(20, 100)]
    [InlineData(100, 500)]
    public void HandoffBound_ScalesWithTheSessionLength(int imageCount, int expected) =>
        Assert.Equal(expected, SessionSetup.HandoffBound(imageCount, maxIds: 1000));

    // "Run it again" redraws from the same array, so even a three-pose session needs more than three
    // images behind it or the second run is the first one reshuffled.
    [Fact]
    public void HandoffBound_HasAFloor() =>
        Assert.Equal(50, SessionSetup.HandoffBound(1, maxIds: 1000));

    // The ceiling is what the handoff can physically carry; a long session cannot vote itself past
    // it (INV-POOL-6).
    [Fact]
    public void HandoffBound_NeverExceedsWhatTheHandoffCarries() =>
        Assert.Equal(1000, SessionSetup.HandoffBound(10_000, maxIds: 1000));

    // The ceiling wins over the floor. Clamping the other way round throws when a caller's limit is
    // below the floor, and "throws" is not a defined outcome for a bound (INV-X-11) — the sibling
    // Sample answers an impossible bound with an empty pool rather than an exception.
    [Theory]
    [InlineData(0, 0)]
    [InlineData(10, 10)]
    [InlineData(-1, 0)]
    public void HandoffBound_WhenTheCeilingIsBelowTheFloor_ReturnsTheCeiling(int maxIds, int expected) =>
        Assert.Equal(expected, SessionSetup.HandoffBound(4, maxIds));

    // The count is a parsed input with no upper bound (INV-SET-1 validates "> 0", nothing more), so
    // a pasted nine-digit number reaches this multiplication. Overflowing it would wrap negative and
    // hand the session an empty pool.
    [Fact]
    public void HandoffBound_DoesNotOverflow() =>
        Assert.Equal(1000, SessionSetup.HandoffBound(int.MaxValue, maxIds: 1000));
}
