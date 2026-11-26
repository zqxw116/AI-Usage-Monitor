using System.Drawing.Drawing2D;

namespace UsageMonitor;

internal sealed partial class MonitorForm
{
    private Point? characterPress;
    private PointF dragOrigin, dragCenter, dragOffset;
    private bool draggingCharacter, dragWasOutside, dragFacingLeft;
    private bool updatingCharacterOverlay;
    private CharacterSnap? dragSnap;
    private CharacterSnapPreview? snapPreview;

    private OrangePose AttachedCharacterPose(bool outside) => orange.PoseAt(outside
        ? orangeDistance / orange.Perimeter(DesignSize) * orange.Perimeter(DesignSize, true) : orangeDistance, DesignSize, outside);

    private void BeginCharacterPress(Point screen, bool fromOutside)
    {
        if (CharacterVisitActive || characterPress is not null) return;
        ClearCharacterHover();
        bool outer = fromOutside && !DesktopCharacter;
        var pose = AttachedCharacterPose(outer);
        float scale = ClientSize.Width / (float)DesignSize.Width;
        var origin = PointToScreen(Point.Empty);
        dragOrigin = DesktopCharacter ? roaming.Position : new PointF(origin.X + pose.Center.X * scale, origin.Y + pose.Center.Y * scale);
        if (fromOutside && outsideCharacter is { Visible: true }) dragOrigin = outsideCharacter.DisplayedCenter;
        dragCenter = dragOrigin;
        dragFacingLeft = DesktopCharacter ? roaming.FacingLeft : (pose.Angle is -180 or 180) != trackReverse;
        dragOffset = new PointF(screen.X - dragOrigin.X, screen.Y - dragOrigin.Y);
        dragWasOutside = fromOutside;
        characterPress = screen;
        if (!fromOutside) Capture = true;
    }

    private void MoveCharacterPress(Point screen)
    {
        if (characterPress is not Point pressed) return;
        if (!draggingCharacter)
        {
            var threshold = SystemInformation.DragSize;
            if (Math.Abs(screen.X - pressed.X) < threshold.Width / 2 && Math.Abs(screen.Y - pressed.Y) < threshold.Height / 2) return;
            draggingCharacter = true;
            visitStarted = null;
        }
        dragCenter = new PointF(screen.X - dragOffset.X, screen.Y - dragOffset.Y);
        float scale = ClientSize.Width / (float)DesignSize.Width;
        dragCenter = CharacterVisual.FitCenter(dragCenter, orange.Height(DesignSize) * scale, 0, CharacterEffect.None,
            Screen.FromPoint(screen).WorkingArea);
        dragSnap = CharacterAttachment.Find(orange, DesignSize, RectangleToScreen(ClientRectangle), scale, screen);
        updatingCharacterOverlay = true;
        try
        {
            if (dragSnap is { } snap)
            {
                snapPreview ??= new CharacterSnapPreview();
                snapPreview.UpdatePreview(this, orange, snap, orange.Height(DesignSize) * scale, orangeTime);
            }
            else snapPreview?.Hide();
            UpdateOutsideCharacter();
        }
        finally { updatingCharacterOverlay = false; }
        if (characterPress is not null && !dragWasOutside && !Capture) Capture = true;
        Invalidate();
    }

    private void EndCharacterPress(Point screen)
    {
        if (characterPress is null) return;
        MoveCharacterPress(screen);
        bool dragged = draggingCharacter, fromOutside = dragWasOutside;
        characterPress = null;
        draggingCharacter = false;
        Capture = false;
        snapPreview?.Hide();
        if (!dragged)
        {
            BeginCharacterVisit(fromOutside);
            return;
        }
        if (dragSnap is { } snap)
        {
            settings.CharacterDisplay = snap.Mode;
            orangeDistance = snap.Distance * orange.Perimeter(DesignSize);
            double horizontal = Math.Cos(snap.Angle * Math.PI / 180);
            if (Math.Abs(horizontal) > .5) trackReverse = (horizontal < 0) != dragFacingLeft;
            roamingInitialized = false;
        }
        else
        {
            settings.CharacterDisplay = settings.CharacterDesktopMovement;
            roaming.Relocate(dragCenter, dragFacingLeft);
            if (settings.CharacterDisplay == CharacterDisplay.FollowMouse) roaming.RequireFollowApproach();
            roamingInitialized = true;
        }
        roamingTime = orangeTime;
        dragSnap = null;
        SaveCharacterPlacement();
        UpdateOutsideCharacter();
        Invalidate();
    }

    private void CancelCharacterPress()
    {
        if (characterPress is null) return;
        characterPress = null;
        draggingCharacter = false;
        dragSnap = null;
        Capture = false;
        outsideCharacter?.CancelPointer();
        snapPreview?.Hide();
        roamingTime = orangeTime;
        UpdateOutsideCharacter();
        Invalidate();
    }

    private void SaveCharacterPlacement()
    {
        settings.CharacterTrackDistance = orangeDistance / orange.Perimeter(DesignSize);
        settings.CharacterFacingLeft = trackReverse;
        if (DesktopCharacter && roamingInitialized)
        {
            settings.CharacterDesktopX = roaming.Position.X;
            settings.CharacterDesktopY = roaming.Position.Y;
            settings.CharacterFacingLeft = roaming.FacingLeft;
        }
        try { if (liveAnimation) settings.Save(); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { MessageBox.Show(this, "캐릭터 위치를 저장하지 못했습니다."); }
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.Button == MouseButtons.Left) EndCharacterPress(PointToScreen(e.Location));
    }

    protected override void OnMouseCaptureChanged(EventArgs e)
    {
        base.OnMouseCaptureChanged(e);
        if (!Capture && !dragWasOutside && !updatingCharacterOverlay) CancelCharacterPress();
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == Keys.Escape && characterPress is not null) { CancelCharacterPress(); return true; }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    private sealed class CharacterSnapPreview : Form
    {
        private OrangeWalker? walker;
        private CharacterSnap snap;
        private float size;
        private double seconds;
        protected override bool ShowWithoutActivation => true;
        protected override CreateParams CreateParams
        {
            get { var p = base.CreateParams; p.ExStyle |= 0x080000A0; return p; }
        }
        internal CharacterSnapPreview()
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            BackColor = TransparencyKey = Color.Magenta;
            Opacity = .65;
            DoubleBuffered = true;
        }
        internal void UpdatePreview(Form owner, OrangeWalker walker, CharacterSnap snap, float size, double seconds)
        {
            this.walker = walker; this.snap = snap; this.size = size; this.seconds = seconds;
            float side = size + 40;
            var visible = RectangleF.Intersect(new RectangleF(snap.Center.X - side / 2, snap.Center.Y - side / 2, side, side), SystemInformation.VirtualScreen);
            if (visible.IsEmpty) { Hide(); return; }
            Bounds = Rectangle.Ceiling(visible);
            TopMost = owner.TopMost;
            Invalidate();
            if (!Visible) Show(owner);
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            var center = new PointF(snap.Center.X - Left, snap.Center.Y - Top);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            var foot = new PointF(snap.Foot.X - Left, snap.Foot.Y - Top);
            using var halo = new Pen(Color.FromArgb(90, 255, 210), 6);
            e.Graphics.DrawEllipse(halo, foot.X - 9, foot.Y - 9, 18, 18);
            walker?.DrawSprite(e.Graphics, center, snap.Angle, seconds, size, smooth: true);
        }
    }
}
