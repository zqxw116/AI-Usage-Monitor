namespace UsageMonitor;

internal enum CharacterEffect { None, Startled, Arrived }

internal static class CharacterVisual
{
    internal static float Stretch(CharacterEffect effect, float reactionProgress = .25f)
    {
        if (effect != CharacterEffect.Startled) return 1;
        float progress = Math.Clamp(reactionProgress, 0, 1);
        float amount = progress < .25f ? progress / .25f : (1 - progress) / .75f;
        return 1 + .06f * amount * amount * (3 - 2 * amount);
    }

    internal static RectangleF SpriteBounds(PointF center, float size, float angle, CharacterEffect effect, float reactionProgress = .25f)
    {
        double radians = angle * Math.PI / 180;
        float cosine = (float)Math.Cos(radians), sine = (float)Math.Sin(radians);
        float half = size / 2, top = half - size * Stretch(effect, reactionProgress);
        PointF At(float x, float y) => new(center.X + x * cosine - y * sine, center.Y + x * sine + y * cosine);
        var points = new[] { At(-half, top), At(half, top), At(-half, half), At(half, half) };
        return RectangleF.FromLTRB(points.Min(point => point.X), points.Min(point => point.Y),
            points.Max(point => point.X), points.Max(point => point.Y));
    }

    internal static RectangleF AlertBounds(RectangleF sprite, float size, RectangleF? clip = null)
    {
        float height = Math.Max(12, size * .32f);
        if (clip is { } limit) height = Math.Min(height, Math.Min(limit.Width, limit.Height) - 2);
        var alert = new RectangleF(sprite.Right + 1, sprite.Top - height * .2f, height * .26f, height);
        if (clip is { } area)
        {
            alert.X = Math.Clamp(alert.X, area.Left + 1, area.Right - alert.Width - 1);
            alert.Y = Math.Clamp(alert.Y, area.Top + 1, area.Bottom - alert.Height - 1);
        }
        return alert;
    }

    internal static RectangleF Bounds(PointF center, float size, float angle, CharacterEffect effect, float reactionProgress = .25f, RectangleF? alertClip = null)
    {
        var sprite = SpriteBounds(center, size, angle, effect, reactionProgress);
        if (effect == CharacterEffect.None) return sprite;
        var alert = effect == CharacterEffect.Arrived ? NoteBounds(sprite, size, reactionProgress, alertClip) : AlertBounds(sprite, size, alertClip);
        alert.Inflate(1, 1);
        return RectangleF.Union(sprite, alert);
    }

    internal static RectangleF NoteBounds(RectangleF sprite, float size, float progress = .25f, RectangleF? clip = null)
    {
        float height = Math.Max(16, size * .32f);
        if (clip is { } limit) height = Math.Min(height, Math.Min(limit.Width, limit.Height) - 2);
        var note = new RectangleF(sprite.Right + 2, sprite.Top - height * (.2f + .2f * Math.Clamp(progress, 0, 1)), height * .78f, height);
        if (clip is { } area)
        {
            note.X = Math.Clamp(note.X, area.Left + 1, area.Right - note.Width - 1);
            note.Y = Math.Clamp(note.Y, area.Top + 1, area.Bottom - note.Height - 1);
        }
        return note;
    }

    internal static PointF FitCenter(PointF center, float size, float angle, CharacterEffect effect, Rectangle bounds, float reactionProgress = .25f)
    {
        var visual = Bounds(center, size, angle, effect, reactionProgress);
        float dx = visual.Width <= bounds.Width ? Math.Clamp(0, bounds.Left - visual.Left, bounds.Right - visual.Right) :
            bounds.Left + bounds.Width / 2f - (visual.Left + visual.Width / 2);
        float dy = visual.Height <= bounds.Height ? Math.Clamp(0, bounds.Top - visual.Top, bounds.Bottom - visual.Bottom) :
            bounds.Top + bounds.Height / 2f - (visual.Top + visual.Height / 2);
        return new PointF(center.X + dx, center.Y + dy);
    }
}
