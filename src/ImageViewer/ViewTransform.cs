using System.Windows;

namespace ImageViewer;

// Zoom is expressed in physical image pixels per physical screen pixel.
internal sealed class ViewTransform
{
    public double Zoom { get; private set; } = 1;
    public Vector Offset { get; private set; }
    public bool IsFit { get; private set; } = true;
    public const double MinZoom = .01;
    public const double MaxZoom = 32;

    public void Fit() { IsFit = true; Offset = default; }
    public void Actual() { IsFit = false; Zoom = 1; Offset = default; }
    public void Layout(Size image, Size viewport)
    {
        if (image.Width <= 0 || image.Height <= 0 || viewport.Width <= 0 || viewport.Height <= 0) return;
        if (IsFit) Zoom = Math.Min(viewport.Width / image.Width, viewport.Height / image.Height);
        Clamp(image, viewport);
    }

    public void ZoomAt(double factor, Point anchor, Size image, Size viewport)
    {
        if (image.Width <= 0 || image.Height <= 0) return;
        var old = Zoom;
        // A fit scale can lie outside manual zoom bounds for very small or large images.
        // Keep wheel zoom continuous instead of jumping to the nearest bound.
        Zoom = Math.Clamp(Zoom * factor, Math.Min(MinZoom, old), Math.Max(MaxZoom, old));
        var relative = anchor - new Point(viewport.Width / 2, viewport.Height / 2);
        Offset = relative - (relative - Offset) * (Zoom / old);
        IsFit = false;
        Clamp(image, viewport);
    }

    public void Pan(Vector delta, Size image, Size viewport)
    {
        Offset += delta;
        Clamp(image, viewport);
    }

    private void Clamp(Size image, Size viewport)
    {
        var x = Math.Max(0, (image.Width * Zoom - viewport.Width) / 2);
        var y = Math.Max(0, (image.Height * Zoom - viewport.Height) / 2);
        Offset = new Vector(Math.Clamp(Offset.X, -x, x), Math.Clamp(Offset.Y, -y, y));
    }
}
