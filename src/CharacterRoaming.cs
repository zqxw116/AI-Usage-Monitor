namespace UsageMonitor;

internal sealed class CharacterRoaming
{
    private readonly Random random;
    private PointF? target;
    private PointF velocity;
    private float speed;

    internal CharacterRoaming(Random? random = null) => this.random = random ?? new Random();

    internal PointF Position { get; private set; }
    internal bool FacingLeft { get; private set; }

    internal void Reset(PointF position)
    {
        Position = position;
        target = null;
        velocity = PointF.Empty;
        FacingLeft = false;
        speed = 18 + 6 * (float)random.NextDouble();
    }

    internal PointF Step(double delta, Rectangle area, float spriteSize, PointF? pointer = null, Rectangle? pointerArea = null)
    {
        if (delta <= 0) return Position;
        delta = Math.Min(delta, .1);
        float half = Math.Max(0, spriteSize / 2);
        float left = Math.Min(area.Left + half, area.Left + area.Width / 2f);
        float right = Math.Max(area.Right - half, area.Left + area.Width / 2f);
        float top = Math.Min(area.Top + half, area.Top + area.Height / 2f);
        float bottom = Math.Max(area.Bottom - half, area.Top + area.Height / 2f);
        PointF Clamp(PointF point) => new(Math.Clamp(point.X, left, right), Math.Clamp(point.Y, top, bottom));
        Position = Clamp(Position);
        var previous = Position;

        if (pointer is PointF cursor)
        {
            var follow = Clamp(new PointF(cursor.X + 36, cursor.Y + 28));
            if (pointerArea is { } screen)
            {
                float centerX = screen.Left + screen.Width / 2f, centerY = screen.Top + screen.Height / 2f;
                follow = new PointF(Math.Clamp(follow.X, Math.Min(screen.Left + half, centerX), Math.Max(screen.Right - half, centerX)),
                    Math.Clamp(follow.Y, Math.Min(screen.Top + half, centerY), Math.Max(screen.Bottom - half, centerY)));
            }
            target = follow;
        }
        else if (target is null || target.Value != Clamp(target.Value) || Distance(Position, target.Value) < 12)
        {
            PointF next = Position;
            double farthest = -1;
            double minimum = Math.Min(160, Distance(new PointF(left, top), new PointF(right, bottom)) * .3);
            for (int i = 0; i < 32; i++)
            {
                var candidate = new PointF(left + (right - left) * (float)random.NextDouble(),
                    top + (bottom - top) * (float)random.NextDouble());
                double distance = Distance(Position, candidate);
                if (distance > farthest) { next = candidate; farthest = distance; }
                if (distance >= minimum) break;
            }
            target = next;
        }

        float x = target.Value.X - Position.X, y = target.Value.Y - Position.Y;
        double remaining = Math.Sqrt(x * x + y * y);
        float desiredSpeed = speed * (float)Math.Min(1, remaining / 50);
        float blend = (float)(1 - Math.Exp(-2.5 * delta));
        float desiredX = remaining > 0 ? x / (float)remaining * desiredSpeed : 0;
        float desiredY = remaining > 0 ? y / (float)remaining * desiredSpeed : 0;
        velocity = new PointF(velocity.X + (desiredX - velocity.X) * blend,
            velocity.Y + (desiredY - velocity.Y) * blend);
        Position = Clamp(new PointF(Position.X + velocity.X * (float)delta, Position.Y + velocity.Y * (float)delta));
        UpdateFacing(previous);
        return Position;
    }

    private void UpdateFacing(PointF previous)
    {
        if (Position.X < previous.X) FacingLeft = true;
        else if (Position.X > previous.X) FacingLeft = false;
    }

    private static double Distance(PointF a, PointF b) =>
        Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));
}
