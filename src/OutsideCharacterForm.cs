using System.ComponentModel;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace UsageMonitor;

// 캐릭터 주변만 차지하는 알파 투명 소유 창. 투명 여백은 입력을 통과시킵니다.
internal sealed class OutsideCharacterForm : Form
{
    private (OrangeWalker Walker, int Frame, float Angle, float Size, Rectangle Clip, PointF Center, bool Smooth, bool MirrorX)? rendered;
    private OrangeWalker? hitWalker;
    private PointF hitCenter;
    private float hitSize, hitAngle;
    private double hitSeconds;
    private bool hitMirrorX;
    private bool clickThrough;
    private IntPtr visitDc, visitBitmap, visitPrevious;
    private Bitmap? visitSurface;
    private Size visitSize;
    private Rectangle visitDirty;
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    internal bool ClickThrough
    {
        get => clickThrough;
        set
        {
            if (clickThrough == value) return;
            clickThrough = value;
            if (IsHandleCreated) UpdateStyles();
        }
    }
    internal event Action? CharacterClicked;
    protected override bool ShowWithoutActivation => true;
    protected override CreateParams CreateParams
    {
        get
        {
            var parameters = base.CreateParams;
            parameters.ExStyle |= 0x08080080; // NOACTIVATE | LAYERED | TOOLWINDOW
            if (clickThrough) parameters.ExStyle |= 0x20;
            return parameters;
        }
    }

    internal OutsideCharacterForm()
    {
        AutoScaleMode = AutoScaleMode.None;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        Text = "AI 사용량 모니터 · 외부 캐릭터";
        Cursor = Cursors.Hand;
    }

    internal void UpdateCharacter(Form owner, OrangeWalker walker, Size design, double seconds, double distance)
    {
        float scale = owner.ClientSize.Width / (float)design.Width;
        double fraction = distance / walker.Perimeter(design);
        var pose = walker.PoseAt(fraction * walker.Perimeter(design, true), design, true);
        Point origin = owner.PointToScreen(Point.Empty);
        UpdatePose(owner, walker, new PointF(origin.X + pose.Center.X * scale, origin.Y + pose.Center.Y * scale),
            walker.Height(design) * scale, pose.Angle, seconds, SystemInformation.VirtualScreen);
    }

    internal void UpdatePose(Form owner, OrangeWalker walker, PointF center, float spriteSize, float angle, double seconds, Rectangle clipping, bool smooth = false, bool visiting = false, bool mirrorX = false)
    {
        if (!owner.Visible || owner.WindowState == FormWindowState.Minimized) { Hide(); return; }
        int side = (int)Math.Ceiling(spriteSize) + 2;
        var full = new Rectangle((int)Math.Round(center.X - side / 2f), (int)Math.Round(center.Y - side / 2f), side, side);
        // 큰 캐릭터도 화면 밖 픽셀까지 할당하지 않습니다. 창의 크기 상한은 추가하지 않습니다.
        var visible = Rectangle.Intersect(full, clipping);
        if (visible.Width <= 0 || visible.Height <= 0) { Hide(); return; }
        if (TopMost != owner.TopMost) TopMost = owner.TopMost;
        var clip = new Rectangle(visible.X - full.X, visible.Y - full.Y, visible.Width, visible.Height);
        var localCenter = smooth ? new PointF(center.X - visible.X, center.Y - visible.Y) : new PointF(side / 2f - clip.X, side / 2f - clip.Y);
        var key = (walker, walker.FrameAt(seconds), angle, spriteSize, clip, localCenter, smooth, mirrorX);
        hitWalker = walker;
        hitCenter = new PointF(visible.X + localCenter.X, visible.Y + localCenter.Y);
        hitSize = spriteSize; hitAngle = angle; hitSeconds = seconds;
        hitMirrorX = mirrorX;
        if (visiting)
        {
            EnsureVisitSurface(clipping.Size);
            GdiFlush(); // 재사용 DIB를 덮어쓰기 전에 이전 GDI 읽기를 완료합니다.
            using (var graphics = Graphics.FromImage(visitSurface!))
            {
                var visibleBuffer = new Rectangle(visible.X - clipping.X, visible.Y - clipping.Y, visible.Width, visible.Height);
                var dirty = visitDirty.IsEmpty ? visibleBuffer : Rectangle.Union(visitDirty, visibleBuffer);
                graphics.CompositingMode = System.Drawing.Drawing2D.CompositingMode.SourceCopy;
                using (var clear = new SolidBrush(Color.Transparent)) graphics.FillRectangle(clear, dirty);
                graphics.CompositingMode = System.Drawing.Drawing2D.CompositingMode.SourceOver;
                graphics.SetClip(visibleBuffer);
                var bufferCenter = new PointF(localCenter.X + visibleBuffer.X, localCenter.Y + visibleBuffer.Y);
                walker.DrawSprite(graphics, bufferCenter, angle, seconds, spriteSize, smooth, mirrorX);
                visitDirty = visibleBuffer;
                graphics.Flush(System.Drawing.Drawing2D.FlushIntention.Sync);
            }
            Present(visible.Size, visible.Location, visitDc, new Point(visible.X - clipping.X, visible.Y - clipping.Y));
            UpdateBounds(); // UpdateLayeredWindow의 원자적 갱신 후 관리 좌표만 동기화합니다.
            rendered = key;
        }
        else if (rendered != key || !IsHandleCreated)
        {
            ReleaseVisitSurface();
            using var surface = new Bitmap(visible.Width, visible.Height, PixelFormat.Format32bppPArgb);
            using (var graphics = Graphics.FromImage(surface))
                walker.DrawSprite(graphics, localCenter, angle, seconds, spriteSize, smooth, mirrorX);
            using var dcSurface = CreateTemporaryDc(surface);
            Present(surface.Size, visible.Location, dcSurface.Dc);
            UpdateBounds();
            rendered = key;
        }
        else if (Location != visible.Location) Location = visible.Location;
        if (!Visible) Show(owner);
    }

    protected override void WndProc(ref Message message)
    {
        if (message.Msg == 0x84)
        {
            long packed = message.LParam.ToInt64();
            var point = new PointF(unchecked((short)(packed & 0xffff)), unchecked((short)((packed >> 16) & 0xffff)));
            message.Result = (IntPtr)(!ClickThrough && hitWalker?.HitTestSprite(point, hitCenter, hitAngle, hitSeconds, hitSize, hitMirrorX) == true ? 1 : -1);
            return;
        }
        if (message.Msg == 0x21) { message.Result = (IntPtr)3; return; } // MA_NOACTIVATE
        base.WndProc(ref message);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button == MouseButtons.Left && !ClickThrough) CharacterClicked?.Invoke();
    }

    internal void ReleaseVisitSurface()
    {
        GdiFlush();
        visitSurface?.Dispose();
        visitSurface = null;
        if (visitDc != IntPtr.Zero)
        {
            if (visitPrevious != IntPtr.Zero) SelectObject(visitDc, visitPrevious);
            if (visitBitmap != IntPtr.Zero) DeleteObject(visitBitmap);
            DeleteDC(visitDc);
        }
        visitDc = visitBitmap = visitPrevious = IntPtr.Zero;
        visitSize = Size.Empty;
        visitDirty = Rectangle.Empty;
        rendered = null;
    }

    private void EnsureVisitSurface(Size size)
    {
        if (visitSurface != null && visitSize == size) return;
        ReleaseVisitSurface();
        visitDc = CreateCompatibleDC(IntPtr.Zero);
        if (visitDc == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
        var info = new BitmapInfo { Header = new BitmapInfoHeader { Size = (uint)Marshal.SizeOf<BitmapInfoHeader>(), Width = size.Width, Height = -size.Height, Planes = 1, BitCount = 32, Compression = 0 } };
        visitBitmap = CreateDIBSection(visitDc, ref info, 0, out var bits, IntPtr.Zero, 0);
        if (visitBitmap == IntPtr.Zero || bits == IntPtr.Zero) { ReleaseVisitSurface(); throw new Win32Exception(Marshal.GetLastWin32Error()); }
        visitPrevious = SelectObject(visitDc, visitBitmap);
        if (visitPrevious == IntPtr.Zero || visitPrevious == new IntPtr(-1)) { ReleaseVisitSurface(); throw new Win32Exception(Marshal.GetLastWin32Error()); }
        try { visitSurface = new Bitmap(size.Width, size.Height, size.Width * 4, PixelFormat.Format32bppPArgb, bits); }
        catch { ReleaseVisitSurface(); throw; }
        visitSize = size;
    }

    private static TemporaryDc CreateTemporaryDc(Bitmap surface)
    {
        var dc = CreateCompatibleDC(IntPtr.Zero);
        if (dc == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
        var bitmap = surface.GetHbitmap(Color.FromArgb(0));
        if (bitmap == IntPtr.Zero) { DeleteDC(dc); throw new Win32Exception(Marshal.GetLastWin32Error()); }
        var previous = SelectObject(dc, bitmap);
        if (previous == IntPtr.Zero || previous == new IntPtr(-1)) { DeleteObject(bitmap); DeleteDC(dc); throw new Win32Exception(Marshal.GetLastWin32Error()); }
        return new TemporaryDc(dc, bitmap, previous);
    }

    private void Present(Size size, Point location, IntPtr dc, Point? sourcePoint = null)
    {
        var source = sourcePoint ?? Point.Empty;
        var blend = new BlendFunction { Alpha = 255, Format = 1 };
        if (!UpdateLayeredWindow(Handle, IntPtr.Zero, ref location, ref size, dc, ref source, 0, ref blend, 2))
            throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    protected override void OnVisibleChanged(EventArgs e) { base.OnVisibleChanged(e); if (!Visible) ReleaseVisitSurface(); }
    protected override void Dispose(bool disposing) { if (disposing) ReleaseVisitSurface(); base.Dispose(disposing); }

    private sealed class TemporaryDc(IntPtr dc, IntPtr bitmap, IntPtr previous) : IDisposable
    {
        public IntPtr Dc => dc;
        public void Dispose() { SelectObject(dc, previous); DeleteObject(bitmap); DeleteDC(dc); }
    }

    [StructLayout(LayoutKind.Sequential)] private struct BitmapInfoHeader { public uint Size; public int Width, Height; public ushort Planes, BitCount; public uint Compression, ImageSize; public int XPelsPerMeter, YPelsPerMeter; public uint ColorsUsed, ColorsImportant; }
    [StructLayout(LayoutKind.Sequential)] private struct BitmapInfo { public BitmapInfoHeader Header; public uint Color; }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct BlendFunction { public byte Operation, Flags, Alpha, Format; }
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UpdateLayeredWindow(IntPtr window, IntPtr screen, ref Point destination, ref Size size,
        IntPtr source, ref Point sourcePoint, int color, ref BlendFunction blend, int flags);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr dc, IntPtr value);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr value);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern bool GdiFlush();
    [DllImport("gdi32.dll", SetLastError = true)] private static extern IntPtr CreateDIBSection(IntPtr dc, ref BitmapInfo info, uint usage, out IntPtr bits, IntPtr section, uint offset);
}
