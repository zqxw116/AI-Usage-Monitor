namespace UsageMonitor;

internal sealed class CharacterRoaming
{
    private readonly Random random;
    private PointF? target;
    private PointF velocity;
    private float speed;
    private bool following, nearby;
    private double reactionAge = double.PositiveInfinity;
    private PointF? pointerAnchor;
    private PointF pointerDirection;
    private bool approachRequired;
    private PointF arrivalCursor;
    private PointF? resumedAt;
    private double arrivalAge = double.PositiveInfinity;
    internal const double ArrivalPause = .45, ArrivalNoteDuration = .65;

    internal CharacterRoaming(Random? random = null) => this.random = random ?? new Random();

    internal PointF Position { get; private set; }
    internal bool FacingLeft { get; private set; }
    internal double AnimationSeconds { get; private set; }
    internal bool Startled => reactionAge < .2;
    internal double ReactionAge => Startled ? reactionAge : double.PositiveInfinity;
    internal bool Nearby => nearby;
    internal bool ArrivalPaused => nearby && arrivalAge < ArrivalPause;
    internal double ArrivalProgress => nearby && arrivalAge < ArrivalNoteDuration
        ? arrivalAge / ArrivalNoteDuration : double.PositiveInfinity;

    internal void Reset(PointF position)
    {
        Position = position;
        target = null;
        velocity = PointF.Empty;
        FacingLeft = false;
        speed = 18 + 6 * (float)random.NextDouble();
        following = nearby = false;
        approachRequired = false;
        resumedAt = null;
        arrivalAge = double.PositiveInfinity;
        reactionAge = double.PositiveInfinity;
        pointerAnchor = null;
        pointerDirection = PointF.Empty;
        AnimationSeconds = 0;
    }

    internal void Relocate(PointF position, bool facingLeft)
    {
        if (speed == 0) speed = 18 + 6 * (float)random.NextDouble();
        Position = position;
        FacingLeft = facingLeft;
        velocity = PointF.Empty;
        target = null;
        following = nearby = false;
        approachRequired = false;
        resumedAt = null;
        arrivalAge = double.PositiveInfinity;
        reactionAge = double.PositiveInfinity;
        pointerAnchor = null;
        pointerDirection = PointF.Empty;
    }

    internal void RequireFollowApproach() => approachRequired = true;

    internal void ConstrainPosition(PointF position) => Position = position;

    internal PointF Step(double delta, Rectangle area, float spriteSize, PointF? pointer = null, Rectangle? pointerArea = null,
        RectangleF? bodyBounds = null)
    {
        if (delta <= 0) return Position;
        delta = Math.Min(delta, .1);
        reactionAge += delta;
        if (nearby) arrivalAge += delta;
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
            var follow = Clamp(cursor);
            if (pointerArea is { } screen)
            {
                float centerX = screen.Left + screen.Width / 2f, centerY = screen.Top + screen.Height / 2f;
                follow = new PointF(Math.Clamp(follow.X, Math.Min(screen.Left + half, centerX), Math.Max(screen.Right - half, centerX)),
                    Math.Clamp(follow.Y, Math.Min(screen.Top + half, centerY), Math.Max(screen.Bottom - half, centerY)));
            }
            double cursorDistance = BodyDistance(cursor, spriteSize, bodyBounds);
            if (cursorDistance > 160) approachRequired = false;
            if (nearby && Distance(arrivalCursor, cursor) > 3)
            {
                nearby = following = false;
                arrivalAge = double.PositiveInfinity;
                resumedAt = Position;
                reactionAge = 0;
            }
            if (!following)
            {
                following = true;
                if (cursorDistance > 12) reactionAge = 0;
                target = null;
                pointerAnchor = cursor;
                pointerDirection = new PointF(cursor.X - Position.X, cursor.Y - Position.Y);
            }
            if (!nearby && !approachRequired && cursorDistance <= 12 &&
                (resumedAt is null || Distance(resumedAt.Value, Position) >= 2))
            {
                nearby = true;
                arrivalAge = 0;
                arrivalCursor = cursor;
                resumedAt = null;
                velocity = PointF.Empty;
                pointerAnchor = null;
                target = null;
            }
            if (nearby)
            {
                if (ArrivalPaused) return Position;
            }
            if (!nearby && pointerAnchor is PointF anchor && Distance(anchor, cursor) >= 12)
            {
                var direction = new PointF(cursor.X - anchor.X, cursor.Y - anchor.Y);
                double length = Distance(PointF.Empty, direction), priorLength = Distance(PointF.Empty, pointerDirection);
                if (priorLength > 0 && (direction.X * pointerDirection.X + direction.Y * pointerDirection.Y) / (length * priorLength) < .5 && reactionAge >= .3)
                    reactionAge = 0;
                pointerDirection = direction;
                pointerAnchor = cursor;
            }
            if (!nearby && (target is null || Distance(target.Value, follow) >= 2))
            {
                target = follow;
            }
        }
        if (nearby) pointer = null;
        if (pointer is null && (target is null || target.Value != Clamp(target.Value) || Distance(Position, target.Value) < 12))
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

        var destination = target!.Value;
        float x = destination.X - Position.X, y = destination.Y - Position.Y;
        double remaining = Math.Sqrt(x * x + y * y);
        double multiplier = 1;
        if (pointer is PointF pointerPosition && !nearby)
        {
            double distance = BodyDistance(pointerPosition, spriteSize, bodyBounds);
            if (distance > 360) multiplier = Math.Min(720 / speed, 6 + (distance - 360) / 120);
            else if (distance > 100)
            {
                double amount = (distance - 100) / 260;
                multiplier = 1 + 5 * amount * amount * (3 - 2 * amount);
            }
            else multiplier = 1;
        }
        float desiredSpeed = speed * (float)(multiplier * (pointer is null ? Math.Min(1, remaining / 50) : 1));
        float blend = (float)(1 - Math.Exp(-(pointer is null ? 2.5 : 8) * delta));
        float desiredX = remaining > 0 ? x / (float)remaining * desiredSpeed : 0;
        float desiredY = remaining > 0 ? y / (float)remaining * desiredSpeed : 0;
        velocity = new PointF(velocity.X + (desiredX - velocity.X) * blend,
            velocity.Y + (desiredY - velocity.Y) * blend);
        if (pointer is not null)
        {
            double currentSpeed = Distance(PointF.Empty, velocity);
            double limit = Math.Min(speed * multiplier, remaining / delta);
            if (currentSpeed > 0)
                velocity = new PointF(velocity.X * (float)(limit / currentSpeed), velocity.Y * (float)(limit / currentSpeed));
            if (remaining < .01)
            {
                velocity = PointF.Empty;
                Position = destination;
            }
        }
        Position = Clamp(new PointF(Position.X + velocity.X * (float)delta, Position.Y + velocity.Y * (float)delta));
        AnimationSeconds += Distance(previous, Position) / speed;
        UpdateFacing(previous);
        return Position;
    }

    private double BodyDistance(PointF cursor, float size, RectangleF? normalized)
    {
        var body = normalized ?? new RectangleF(0, 0, 1, 1);
        float x = FacingLeft ? 1 - body.Right : body.Left;
        var bounds = new RectangleF(Position.X + (x - .5f) * size, Position.Y + (body.Top - .5f) * size,
            body.Width * size, body.Height * size);
        double dx = Math.Max(Math.Max(bounds.Left - cursor.X, cursor.X - bounds.Right), 0);
        double dy = Math.Max(Math.Max(bounds.Top - cursor.Y, cursor.Y - bounds.Bottom), 0);
        return Math.Sqrt(dx * dx + dy * dy);
    }

    private void UpdateFacing(PointF previous)
    {
        if (Position.X < previous.X) FacingLeft = true;
        else if (Position.X > previous.X) FacingLeft = false;
    }

    private static double Distance(PointF a, PointF b) =>
        Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));
}
