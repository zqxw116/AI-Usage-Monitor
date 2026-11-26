namespace UsageMonitor;

internal sealed partial class MonitorForm
{
    private bool characterHovered;
    private double hoverLeftAt = double.NegativeInfinity, lastHoverReaction = double.NegativeInfinity;
    private double? hoverReactionStarted;
    private RectangleF reactionDamage;
    private double characterAnimationTime;
    private bool CharacterHoverPaused => characterHovered && characterPress is null && !CharacterVisitActive;

    private double CurrentCharacterReactionAge => CharacterVisitActive ? double.PositiveInfinity :
        characterHovered ? hoverReactionStarted is double start ? orangeTime - start : double.PositiveInfinity :
            DesktopCharacter ? roaming.ReactionAge : double.PositiveInfinity;
    private CharacterEffect CurrentCharacterEffect => CharacterVisitActive || characterPress is not null ? CharacterEffect.None :
        CharacterHoverPaused ? CharacterEffect.Startled : DesktopCharacter && double.IsFinite(roaming.ArrivalProgress)
            ? CharacterEffect.Arrived : CurrentCharacterReactionAge is >= 0 and < .2 ? CharacterEffect.Startled : CharacterEffect.None;
    private float CurrentCharacterReactionProgress => CurrentCharacterEffect == CharacterEffect.Arrived
        ? (float)roaming.ArrivalProgress : (float)Math.Clamp(CurrentCharacterReactionAge / .2, 0, 1);

    private void AdvanceCharacter(double now, Point pointer)
    {
        UpdateCharacterHover(pointer);
        if (!CharacterVisitActive && characterPress is null && !CharacterHoverPaused)
        {
            double delta = Math.Max(0, now - orangeTime);
            orangeDistance += delta * (trackReverse ? -40 : 40);
            characterAnimationTime += delta;
        }
        orangeTime = now;
        UpdateOutsideCharacter(pointer);
        UpdateCharacterHover(pointer);
    }

    private void UpdateCharacterHover(Point pointer)
    {
        if (!Visible || WindowState == FormWindowState.Minimized || CharacterVisitActive || characterPress is not null)
        { SetCharacterHover(false); return; }
        var local = PointToClient(pointer);
        SetCharacterHover(ClientRectangle.Contains(local) && HitInsideCharacter(LogicalPoint(local)) ||
            outsideCharacter?.ContainsBody(pointer) == true);
    }

    private void SetCharacterHover(bool hovered)
    {
        if (characterHovered == hovered) return;
        characterHovered = hovered;
        if (!hovered) hoverLeftAt = orangeTime;
        else if (orangeTime - hoverLeftAt >= .15 && orangeTime - lastHoverReaction >= .3 && !CharacterVisitActive)
            hoverReactionStarted = lastHoverReaction = orangeTime;
        roamingTime = orangeTime;
        UpdateOutsideCharacter();
        Invalidate();
    }

    private void ClearCharacterHover()
    {
        characterHovered = false;
        hoverReactionStarted = null;
        hoverLeftAt = lastHoverReaction = double.NegativeInfinity;
        reactionDamage = RectangleF.Empty;
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        if (characterPress is null) UpdateCharacterHover(Cursor.Position);
    }
}
