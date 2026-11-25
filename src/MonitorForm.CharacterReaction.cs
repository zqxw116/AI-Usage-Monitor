namespace UsageMonitor;

internal sealed partial class MonitorForm
{
    private bool characterHovered;
    private double hoverLeftAt = double.NegativeInfinity, lastHoverReaction = double.NegativeInfinity;
    private double? hoverReactionStarted;
    private RectangleF reactionDamage;

    private double CurrentCharacterReactionAge => CharacterVisitActive ? double.PositiveInfinity :
        Math.Min(hoverReactionStarted is double start ? orangeTime - start : double.PositiveInfinity,
            DesktopCharacter ? roaming.ReactionAge : double.PositiveInfinity);
    private CharacterEffect CurrentCharacterEffect => !CharacterVisitActive && DesktopCharacter && double.IsFinite(roaming.ArrivalProgress)
        ? CharacterEffect.Arrived : CurrentCharacterReactionAge is >= 0 and < .2 ? CharacterEffect.Startled : CharacterEffect.None;
    private float CurrentCharacterReactionProgress => CurrentCharacterEffect == CharacterEffect.Arrived
        ? (float)roaming.ArrivalProgress : (float)(CurrentCharacterReactionAge / .2);

    private void SetCharacterHover(bool hovered)
    {
        if (characterHovered == hovered) return;
        characterHovered = hovered;
        if (!hovered) { hoverLeftAt = orangeTime; return; }
        if (orangeTime - hoverLeftAt < .15 || orangeTime - lastHoverReaction < .3 || CharacterVisitActive) return;
        hoverReactionStarted = lastHoverReaction = orangeTime;
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
        if (characterPress is null) SetCharacterHover(false);
    }
}
