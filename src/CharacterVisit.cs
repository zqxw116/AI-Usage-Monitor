namespace UsageMonitor;

internal readonly record struct CharacterVisitPose(PointF Center, float Size, float Angle);

internal static class CharacterVisit
{
    internal const double ExpandDuration = .85;
    internal const double HoldDuration = .65;
    internal const double ReturnDuration = .55;
    internal const double Duration = ExpandDuration + HoldDuration + ReturnDuration;

    internal static CharacterVisitPose PoseAt(double elapsed, PointF origin, float originalSize,
        float originalAngle, Rectangle workingArea, RectangleF face, bool mirrorX = false)
    {
        var original = new CharacterVisitPose(origin, originalSize, originalAngle);
        if (elapsed <= 0 || elapsed >= Duration) return original;
        float size = Math.Max(originalSize, Math.Min(workingArea.Width * .45f / face.Width, workingArea.Height * .45f / face.Height));
        float faceCenterX = face.Left + face.Width / 2;
        if (mirrorX) faceCenterX = 1 - faceCenterX;
        var center = new PointF(workingArea.Left + workingArea.Width / 2f - (faceCenterX - .5f) * size,
            workingArea.Top + workingArea.Height / 2f - (face.Top + face.Height / 2 - .5f) * size);
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

    private static CharacterVisitPose Blend(CharacterVisitPose from, CharacterVisitPose to, double progress)
    {
        float amount = (float)progress;
        return new CharacterVisitPose(new PointF(from.Center.X + (to.Center.X - from.Center.X) * amount,
            from.Center.Y + (to.Center.Y - from.Center.Y) * amount),
            from.Size + (to.Size - from.Size) * amount, from.Angle + (to.Angle - from.Angle) * amount);
    }
}
