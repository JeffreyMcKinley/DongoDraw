namespace FigureDrawing.Core;

public sealed class ViewerTools
{
    public const double MinZoom = 1.0;
    public const double MaxZoom = 2.5;
    public const double ZoomStep = 0.2;

    public ViewerTools(bool grayscale = false) => Grayscale = grayscale;

    public bool Grayscale { get; private set; }

    public bool Flip { get; private set; }

    public bool Grid { get; private set; }

    public bool Blur { get; private set; }

    public double Zoom { get; private set; } = MinZoom;

    public bool CanZoomIn => Zoom < MaxZoom - Epsilon;
    public bool CanZoomOut => Zoom > MinZoom + Epsilon;

    public void ToggleGrayscale() => Grayscale = !Grayscale;
    public void ToggleFlip() => Flip = !Flip;
    public void ToggleGrid() => Grid = !Grid;
    public void ToggleBlur() => Blur = !Blur;

    public void ZoomIn() => Zoom = Clamp(Zoom + ZoomStep);
    public void ZoomOut() => Zoom = Clamp(Zoom - ZoomStep);

    public void ResetZoom() => Zoom = MinZoom;

    const double Epsilon = 1e-9;

    // Rounded, not just clamped: unrounded stepping drifts and never lands on a bound (INV-VIEW-2).
    static double Clamp(double value) => Math.Round(Math.Clamp(value, MinZoom, MaxZoom), 2);
}
