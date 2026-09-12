using DongoDraw.Core;

namespace DongoDraw.Tests;

// The viewing aids on the player screen (grayscale, flip, grid, blur, zoom). Toggling is trivial;
// the zoom range and step are the part with a rule, and the part a screen would otherwise get wrong.
public class ViewerToolsTests
{
    [Fact]
    public void Defaults_AreEverythingOff_AtFitToScreen()
    {
        var tools = new ViewerTools();

        Assert.False(tools.Grayscale);
        Assert.False(tools.Flip);
        Assert.False(tools.Grid);
        Assert.False(tools.Blur);
        Assert.Equal(ViewerTools.MinZoom, tools.Zoom);
    }

    // "Start in grayscale" is a persisted preference, so the viewer must be constructible already on.
    [Fact]
    public void SeededGrayscale_StartsOn()
    {
        var tools = new ViewerTools(grayscale: true);

        Assert.True(tools.Grayscale);
    }

    [Fact]
    public void EachToggle_FlipsOnlyItsOwnFlag()
    {
        var tools = new ViewerTools();

        tools.ToggleGrayscale();
        Assert.True(tools.Grayscale);
        Assert.False(tools.Flip);
        Assert.False(tools.Grid);
        Assert.False(tools.Blur);

        tools.ToggleFlip();
        tools.ToggleGrid();
        tools.ToggleBlur();
        Assert.True(tools.Flip);
        Assert.True(tools.Grid);
        Assert.True(tools.Blur);

        tools.ToggleGrayscale();
        Assert.False(tools.Grayscale);
        Assert.True(tools.Flip);
    }

    [Fact]
    public void ZoomIn_StepsUp_ByOneStep()
    {
        var tools = new ViewerTools();

        tools.ZoomIn();

        Assert.Equal(ViewerTools.MinZoom + ViewerTools.ZoomStep, tools.Zoom, 3);
    }

    [Fact]
    public void ZoomOut_NeverGoesBelowFitToScreen()
    {
        var tools = new ViewerTools();

        tools.ZoomOut();
        tools.ZoomOut();

        Assert.Equal(ViewerTools.MinZoom, tools.Zoom);
        Assert.False(tools.CanZoomOut);
    }

    [Fact]
    public void ZoomIn_StopsAtTheMaximum()
    {
        var tools = new ViewerTools();

        for (var i = 0; i < 50; i++)
            tools.ZoomIn();

        Assert.Equal(ViewerTools.MaxZoom, tools.Zoom);
        Assert.False(tools.CanZoomIn);
    }

    // Repeated stepping through binary fractions (1 + 0.2 + 0.2 + ...) would otherwise drift and
    // leave the zoom just short of its bound forever.
    [Fact]
    public void SteppingAllTheWayUpAndBackDown_LandsExactlyOnTheBounds()
    {
        var tools = new ViewerTools();

        while (tools.CanZoomIn)
            tools.ZoomIn();
        Assert.Equal(ViewerTools.MaxZoom, tools.Zoom);

        while (tools.CanZoomOut)
            tools.ZoomOut();
        Assert.Equal(ViewerTools.MinZoom, tools.Zoom);
    }

    [Fact]
    public void ResetZoom_ReturnsToFitToScreen_LeavingTheOtherToolsAlone()
    {
        var tools = new ViewerTools();
        tools.ToggleGrid();
        tools.ZoomIn();
        tools.ZoomIn();

        tools.ResetZoom();

        Assert.Equal(ViewerTools.MinZoom, tools.Zoom);
        Assert.True(tools.Grid);
    }

    [Fact]
    public void CanZoomIn_IsTrueAtTheBottomOfTheRange()
    {
        var tools = new ViewerTools();

        Assert.True(tools.CanZoomIn);
        Assert.False(tools.CanZoomOut);
    }
}
