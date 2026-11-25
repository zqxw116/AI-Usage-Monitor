namespace UsageMonitor;

internal readonly record struct CharacterVisitPose(PointF Center, float Size, float Angle);

internal static class CharacterVisit
{
    internal const double ExpandDuration = .85;
    internal const double HoldDuration = .65;
    internal const double ReturnDuration = .55;
    internal const double Duration = ExpandDuration + HoldDuration + ReturnDuration;

    internal static CharacterVisitPose PoseAt(double elapsed, PointF origin, float originalSize,
        float originalAngle, Rectangle workingArea, RectangleF face)
    {
        var original = new CharacterVisitPose(origin, originalSize, originalAngle);
        if (elapsed <= 0 || elapsed >= Duration) return original;
        var target = TargetBounds(workingArea);
        float size = Math.Min(target.Width, target.Height);
        var center = new PointF(target.Left + target.Width / 2, target.Top + target.Height / 2);
        var close = new CharacterVisitPose(center, size, 0);
        if (elapsed < ExpandDuration)
        {
            double progress = elapsed / ExpandDuration;
            return Blend(original, close, progress * progress * (3 - 2 * progress));
        }
        if (elapsed <= ExpandDuration + HoldDuration) return close;
        double returning = (elapsed - ExpandDuration - HoldDuration) / ReturnDuration;
        return Blend(close, original, returning * returning * returning);
    }

    internal static RectangleF TargetBounds(Rectangle workingArea) => new(
        workingArea.Left + workingArea.Width * (688f / 1911), workingArea.Top + workingArea.Height * (464f / 1083),
        workingArea.Width * (702f / 1911), workingArea.Height * (619f / 1083));

    private static CharacterVisitPose Blend(CharacterVisitPose from, CharacterVisitPose to, double progress)
    {
        float amount = (float)progress;
        return new CharacterVisitPose(new PointF(from.Center.X + (to.Center.X - from.Center.X) * amount,
            from.Center.Y + (to.Center.Y - from.Center.Y) * amount),
            from.Size + (to.Size - from.Size) * amount, from.Angle + (to.Angle - from.Angle) * amount);
    }
}
