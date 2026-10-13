using System;
using System.Drawing;

namespace UsageMonitor;

internal static class DesktopBounds
{
    internal static Rectangle Envelope(Rectangle[] areas)
    {
        if (areas.Length == 0) return Rectangle.Empty;
        var envelope = areas[0];
        for (int i = 1; i < areas.Length; i++) envelope = Rectangle.Union(envelope, areas[i]);
        return envelope;
    }

    internal static Rectangle Area(Rectangle proposed, Rectangle[] areas)
    {
        if (areas.Length == 0) return Rectangle.Empty;
        var envelope = Envelope(areas);
        int top = int.MinValue, bottom = int.MaxValue;
        bool overlaps = false;
        foreach (var area in areas)
        {
            if (proposed.Left >= area.Right || proposed.Right <= area.Left) continue;
            overlaps = true;
            top = Math.Max(top, area.Top);
            bottom = Math.Min(bottom, area.Bottom);
        }
        var nearest = Nearest(proposed, areas);
        if (overlaps && top >= bottom) return nearest;
        if (!overlaps)
        {
            // 화면 사이 빈틈에서는 실제 화면 안으로 이동합니다.
            if (proposed.Left >= envelope.Left && proposed.Right <= envelope.Right) return nearest;
            top = nearest.Top;
            bottom = nearest.Bottom;
        }
        return Rectangle.FromLTRB(envelope.Left, top, envelope.Right, bottom);
    }

    internal static Rectangle Fit(Rectangle proposed, Rectangle[] areas, Size design, int edge = 0)
    {
        if (areas.Length == 0) return proposed;
        var fitted = proposed;
        for (int i = 0; i < areas.Length + 2; i++)
        {
            fitted = FitArea(fitted, Area(fitted, areas), design, edge);
            if (Area(fitted, areas).Contains(fitted)) return fitted;
        }
        // 최소 배율 때문에 경계에서 왕복할 경우 한 화면 안에 고정합니다.
        return FitArea(fitted, Nearest(fitted, areas), design, edge);
    }

    private static Rectangle Nearest(Rectangle proposed, Rectangle[] areas)
    {
        var nearest = areas[0];
        double minimum = double.PositiveInfinity;
        foreach (var area in areas)
        {
            double dx = Math.Max(0, Math.Max((double)area.Left - proposed.Right, (double)proposed.Left - area.Right));
            double dy = Math.Max(0, Math.Max((double)area.Top - proposed.Bottom, (double)proposed.Top - area.Bottom));
            double distance = dx * dx + dy * dy;
            if (distance >= minimum) continue;
            minimum = distance;
            nearest = area;
        }
        return nearest;
    }

    private static Rectangle FitArea(Rectangle proposed, Rectangle area, Size design, int edge)
    {
        double maxScale = Math.Min(area.Width / (double)design.Width, area.Height / (double)design.Height);
        double scale = edge is 3 or 6 ? proposed.Height / (double)design.Height : proposed.Width / (double)design.Width;
        scale = Math.Clamp(scale, Math.Min(0.65, maxScale), maxScale);
        int width = Math.Max(1, Math.Min(area.Width, (int)Math.Round(design.Width * scale)));
        int height = Math.Max(1, Math.Min(area.Height, (int)Math.Round(design.Height * scale)));
        int x = edge is 1 or 4 or 7 ? proposed.Right - width : proposed.Left;
        int y = edge is 3 or 4 or 5 ? proposed.Bottom - height : proposed.Top;
        x = Math.Clamp(x, area.Left, area.Right - width);
        y = Math.Clamp(y, area.Top, area.Bottom - height);
        return new Rectangle(x, y, width, height);
    }
}
