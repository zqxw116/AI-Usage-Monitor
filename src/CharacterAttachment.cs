namespace UsageMonitor;

internal readonly record struct CharacterSnap(CharacterDisplay Mode, double Distance, PointF Center, float Angle, PointF Foot, double Separation);

internal static class CharacterAttachment
{
    internal static PointF Foot(PointF center, float size, float angle)
    {
        double radians = angle * Math.PI / 180;
        return new PointF(center.X - (float)Math.Sin(radians) * size / 2, center.Y + (float)Math.Cos(radians) * size / 2);
    }

    internal static CharacterSnap? Find(OrangeWalker walker, Size design, Rectangle screenBounds, float scale, PointF cursor, float threshold = 32)
    {
        bool outside = !((RectangleF)screenBounds).Contains(cursor);
        double dxBox = Math.Max(Math.Max(screenBounds.Left - cursor.X, cursor.X - screenBounds.Right), 0);
        double dyBox = Math.Max(Math.Max(screenBounds.Top - cursor.Y, cursor.Y - screenBounds.Bottom), 0);
        double separation = Math.Sqrt(dxBox * dxBox + dyBox * dyBox);
        if (outside && separation > threshold) return null;
        double perimeter = walker.Perimeter(design, outside);
        float inset = outside ? -walker.Height(design) / 2 : Math.Min(walker.Height(design) / 2 + 2, Math.Min(design.Width, design.Height) / 2f - 2);
        double horizontal = design.Width - inset * 2, vertical = design.Height - inset * 2;
        double[] starts = [0, horizontal, horizontal + vertical, 2 * horizontal + vertical];
        double[] lengths = [horizontal, vertical, horizontal, vertical];
        CharacterSnap? closest = null;
        double closestDistance = double.PositiveInfinity;
        PointF Screen(PointF p) => new(screenBounds.Left + p.X * scale, screenBounds.Top + p.Y * scale);
        for (int i = 0; i < 4; i++)
        {
            var begin = walker.PoseAt(starts[i] + .001, design, outside);
            var end = walker.PoseAt(starts[i] + lengths[i] - .001, design, outside);
            var a = Screen(begin.Center);
            var b = Screen(end.Center);
            double dx = b.X - a.X, dy = b.Y - a.Y;
            double amount = dx * dx + dy * dy > 0 ? Math.Clamp(((cursor.X - a.X) * dx + (cursor.Y - a.Y) * dy) / (dx * dx + dy * dy), 0, 1) : 0;
            double distance = starts[i] + .001 + (lengths[i] - .002) * amount;
            var pose = walker.PoseAt(distance, design, outside);
            var center = Screen(pose.Center);
            var anchor = Foot(center, walker.Height(design) * scale, pose.Angle);
            double cursorDistance = Math.Pow(cursor.X - center.X, 2) + Math.Pow(cursor.Y - center.Y, 2);
            var candidate = new CharacterSnap(outside ? CharacterDisplay.Outside : CharacterDisplay.Inside,
                distance / perimeter, center, pose.Angle, anchor, separation);
            if (cursorDistance < closestDistance) { closest = candidate; closestDistance = cursorDistance; }
        }
        return closest;
    }
}
