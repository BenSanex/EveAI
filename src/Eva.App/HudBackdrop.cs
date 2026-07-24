using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Eva.App;

public sealed class HudBackdrop : Control
{
    private static readonly IBrush BackgroundBrush = new LinearGradientBrush
    {
        StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
        EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
        GradientStops =
        {
            new GradientStop(Color.FromArgb(238, 1, 9, 20), 0),
            new GradientStop(Color.FromArgb(225, 2, 24, 39), 0.52),
            new GradientStop(Color.FromArgb(240, 1, 8, 18), 1)
        }
    };
    private static readonly Pen GridPen = new(
        new SolidColorBrush(Color.FromArgb(22, 39, 190, 230)), 1);
    private static readonly Pen RingPen = new(
        new SolidColorBrush(Color.FromArgb(28, 52, 215, 247)), 1);
    private static readonly Pen ScanPen = new(
        new SolidColorBrush(Color.FromArgb(13, 122, 225, 255)), 1);

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        context.FillRectangle(BackgroundBrush, new Rect(Bounds.Size));

        const double grid = 48;
        for (double x = 0; x < Bounds.Width; x += grid)
        {
            context.DrawLine(GridPen, new Point(x, 0), new Point(x, Bounds.Height));
        }
        for (double y = 0; y < Bounds.Height; y += grid)
        {
            context.DrawLine(GridPen, new Point(0, y), new Point(Bounds.Width, y));
        }
        for (double y = 4; y < Bounds.Height; y += 4)
        {
            context.DrawLine(ScanPen, new Point(0, y), new Point(Bounds.Width, y));
        }

        var center = new Point(Bounds.Width * 0.82, Bounds.Height * 0.22);
        foreach (var radius in new[] { 85d, 145d, 215d })
        {
            context.DrawEllipse(null, RingPen, center, radius, radius);
        }
        context.DrawLine(RingPen, new Point(center.X - 250, center.Y), new Point(center.X + 250, center.Y));
        context.DrawLine(RingPen, new Point(center.X, center.Y - 250), new Point(center.X, center.Y + 250));
    }
}

public sealed class HudChrome : Control
{
    private static readonly IBrush FillBrush =
        new SolidColorBrush(Color.FromArgb(128, 2, 21, 35));
    private static readonly Pen BorderPen =
        new(new SolidColorBrush(Color.FromArgb(195, 31, 183, 222)), 1);
    private static readonly Pen AccentPen =
        new(new SolidColorBrush(Color.FromArgb(230, 62, 219, 251)), 2);

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        if (Bounds.Width <= 1 || Bounds.Height <= 1)
        {
            return;
        }

        var cut = Math.Min(18, Math.Min(Bounds.Width, Bounds.Height) / 3);
        var geometry = new StreamGeometry();
        using (var figure = geometry.Open())
        {
            figure.BeginFigure(new Point(cut, 0), true);
            figure.LineTo(new Point(Bounds.Width - cut, 0));
            figure.LineTo(new Point(Bounds.Width, cut));
            figure.LineTo(new Point(Bounds.Width, Bounds.Height - cut));
            figure.LineTo(new Point(Bounds.Width - cut, Bounds.Height));
            figure.LineTo(new Point(cut, Bounds.Height));
            figure.LineTo(new Point(0, Bounds.Height - cut));
            figure.LineTo(new Point(0, cut));
            figure.EndFigure(true);
        }
        context.DrawGeometry(FillBrush, BorderPen, geometry);

        var accentLength = Math.Min(90, Math.Max(24, Bounds.Width * 0.12));
        context.DrawLine(AccentPen, new Point(cut, 0), new Point(cut + accentLength, 0));
        context.DrawLine(
            AccentPen,
            new Point(Bounds.Width - cut - accentLength, Bounds.Height),
            new Point(Bounds.Width - cut, Bounds.Height));
    }
}
