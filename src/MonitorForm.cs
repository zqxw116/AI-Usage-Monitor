using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Diagnostics;
using DisplayLimits = UsageMonitor.DesktopBounds;

namespace UsageMonitor;

internal sealed class MonitorForm : Form
{
    private const int ServiceWidth = 500;
    private const int RowHeight = 50;
    private const int ToolsWidth = 94;
    private static readonly Color Background = Color.FromArgb(32, 38, 46);
    private static readonly Color LabelColor = Color.FromArgb(220, 228, 236);
    private static readonly Color TimeColor = Color.FromArgb(207, 217, 228);
    private static readonly Color Muted = Color.FromArgb(149, 161, 174);
    private static readonly TimeZoneInfo Korea = TimeZoneInfo.FindSystemTimeZoneById("Korea Standard Time");
    private readonly Icon applicationIcon = LoadApplicationIcon();
    private readonly Bitmap gptLogo = LoadLogo("GptLogo.png");
    private readonly Bitmap claudeLogo = LoadLogo("ClaudeLogo.png");
    private readonly UsageSource codex;
    private readonly UsageSource claude;
    private readonly System.Windows.Forms.Timer timer = new() { Interval = 1000 };
    private readonly System.Windows.Forms.Timer orangeTimer = new() { Interval = 33 };
    private OrangeWalker orange = new(OrangeWalker.Characters[4]);
    private readonly ContextMenuStrip characterMenu = new() { ImageScalingSize = new Size(24, 24) };
    private readonly List<Image> characterIcons = new();
    private int selectedCharacter = 4;
    private NotifyIcon? trayIcon;
    private ContextMenuStrip? trayMenu;
    private readonly Stopwatch orangeClock = new();
    private readonly bool liveAnimation;
    private double orangeDistance, orangeTime;
    private readonly ToolTip tooltip = new() { AutoPopDelay = 20000, InitialDelay = 300, ReshowDelay = 100 };
    // 픽셀 글꼴과 단일 배율로 모든 요소를 함께 확대·축소합니다.
    private readonly Font labelFont = new("맑은 고딕", 11.5f, FontStyle.Bold, GraphicsUnit.Pixel);
    private readonly Font valueFont = new("맑은 고딕", 12.5f, FontStyle.Bold, GraphicsUnit.Pixel);
    private readonly Font timeFont = new("맑은 고딕", 12f, FontStyle.Regular, GraphicsUnit.Pixel);
    private readonly Font brandFont = new("Segoe UI", 13.5f, FontStyle.Bold, GraphicsUnit.Pixel);
    private readonly Font buttonFont = new("맑은 고딕", 12f, FontStyle.Regular, GraphicsUnit.Pixel);
    private bool vertical;
    private int hoverPart = -1;
    internal Size DesignSize => new(vertical ? ServiceWidth + ToolsWidth : ServiceWidth * 2 + ToolsWidth,
        vertical ? RowHeight * 2 : RowHeight);
    internal int OrangeFrame => orange.FrameAt(orangeTime);
    internal int OrangeAngle => (int)orange.PoseAt(orangeDistance, DesignSize).Angle;
    internal float OrangeHeight => orange.Height(DesignSize);
    internal int SelectedCharacter => selectedCharacter;
    internal bool TrayVisible => trayIcon?.Visible == true;
    internal bool TrayUsesApplicationIcon => ReferenceEquals(trayIcon?.Icon, applicationIcon);
    internal static Rectangle[] WorkAreas => Screen.AllScreens.Select(screen => screen.WorkingArea).ToArray();
    internal static bool WithinDesktop(Rectangle rectangle) => DisplayLimits.Area(rectangle, WorkAreas).Contains(rectangle);

    [StructLayout(LayoutKind.Sequential)]
    internal struct NativeRect
    {
        public int Left, Top, Right, Bottom;
        public readonly Rectangle Rectangle => Rectangle.FromLTRB(Left, Top, Right, Bottom);
        public NativeRect(Rectangle rectangle) { Left = rectangle.Left; Top = rectangle.Top; Right = rectangle.Right; Bottom = rectangle.Bottom; }
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)]
    private struct MinMaxInfo { public NativePoint Reserved, MaxSize, MaxPosition, MinTrackSize, MaxTrackSize; }

    [DllImport("user32.dll")] private static extern bool ReleaseCapture();
    [DllImport("user32.dll")] internal static extern IntPtr SendMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam);

    public MonitorForm(UsageSource codex, UsageSource claude, bool live = true)
    {
        this.codex = codex;
        this.claude = claude;
        liveAnimation = live;
        Text = "AI 사용량 모니터";
        AutoScaleMode = AutoScaleMode.None;
        FormBorderStyle = FormBorderStyle.None;
        MaximizeBox = false;
        ClientSize = DesignSize;
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Background;
        DoubleBuffered = true;
        ShowInTaskbar = false;
        Icon = applicationIcon;
        AccessibleName = "Codex와 Claude Code 사용량 모니터";
        codex.Changed += RefreshDisplay;
        claude.Changed += RefreshDisplay;
        for (int i = 0; i < OrangeWalker.Characters.Length; i++)
        {
            int selection = i;
            using var preview = new OrangeWalker(OrangeWalker.Characters[i]);
            var icon = preview.Thumbnail();
            characterIcons.Add(icon);
            var item = new ToolStripMenuItem(OrangeWalker.Characters[i].Name, icon) { Checked = i == selectedCharacter };
            item.Click += (_, _) => SelectCharacter(selection);
            characterMenu.Items.Add(item);
        }
        if (live) CreateTray();
        orangeTimer.Tick += (_, _) =>
        {
            var before = orange.PoseAt(orangeDistance, DesignSize).Bounds;
            double now = orangeClock.Elapsed.TotalSeconds;
            orangeDistance += (now - orangeTime) * 40;
            orangeTime = now;
            var after = orange.PoseAt(orangeDistance, DesignSize).Bounds;
            var damage = RectangleF.Union(before, after);
            float sx = ClientSize.Width / (float)DesignSize.Width, sy = ClientSize.Height / (float)DesignSize.Height;
            var region = Rectangle.Ceiling(new RectangleF(damage.X * sx, damage.Y * sy, damage.Width * sx, damage.Height * sy));
            region.Inflate(2, 2);
            Invalidate(region);
        };
        timer.Tick += async (_, _) =>
        {
            Invalidate();
            await Task.WhenAll(codex.TickAsync(), claude.TickAsync());
        };
        if (live) Shown += async (_, _) =>
        {
            timer.Start();
            orangeClock.Start();
            orangeTimer.Start();
            await Task.WhenAll(codex.TickAsync(true), claude.TickAsync(true));
        };
    }

    private void RefreshDisplay()
    {
        if (IsDisposed) return;
        Invalidate();
        hoverPart = -1;
    }

    internal void ToggleLayout()
    {
        double scale = ClientSize.Width / (double)DesignSize.Width;
        double fraction = orangeDistance / orange.Perimeter(DesignSize);
        vertical = !vertical;
        orangeDistance = fraction * orange.Perimeter(DesignSize);
        var size = DesignSize;
        SetBounds(Left, Top, (int)Math.Round(size.Width * scale), (int)Math.Round(size.Height * scale));
        hoverPart = -1;
        Invalidate();
    }

    internal void ChangeOrangeSize(int delta)
    {
        double fraction = orangeDistance / orange.Perimeter(DesignSize);
        orange.ChangeSize(delta, DesignSize);
        orangeDistance = fraction * orange.Perimeter(DesignSize);
        Invalidate();
    }

    internal void SelectCharacter(int index)
    {
        if (selectedCharacter == index) return;
        double fraction = orangeDistance / orange.Perimeter(DesignSize);
        var replacement = new OrangeWalker(OrangeWalker.Characters[index], orange.PreferredSize);
        orange.Dispose();
        orange = replacement;
        selectedCharacter = index;
        orangeDistance = fraction * orange.Perimeter(DesignSize);
        for (int i = 0; i < characterMenu.Items.Count; i++) ((ToolStripMenuItem)characterMenu.Items[i]).Checked = i == index;
        if (trayIcon is not null) trayIcon.Text = "AI 사용량 · " + OrangeWalker.Characters[index].Name;
        Invalidate();
    }

    internal void CreateTray()
    {
        if (trayIcon is not null) return;
        trayMenu = new ContextMenuStrip();
        trayMenu.Items.Add("창 표시 / 숨기기", null, (_, _) => { if (Visible) HideToTray(); else RestoreFromTray(); });
        var pin = new ToolStripMenuItem("최상단 고정");
        pin.Click += (_, _) => { TogglePin(); pin.Checked = TopMost; };
        trayMenu.Items.Add(pin);
        var characters = new ToolStripMenuItem("캐릭터 선택");
        for (int i = 0; i < OrangeWalker.Characters.Length; i++)
        {
            int selection = i;
            characters.DropDownItems.Add(OrangeWalker.Characters[i].Name, null, (_, _) => SelectCharacter(selection));
        }
        trayMenu.Items.Add(characters);
        trayMenu.Items.Add(new ToolStripSeparator());
        trayMenu.Items.Add("종료", null, (_, _) => Close());
        trayMenu.Opening += (_, _) =>
        {
            pin.Checked = TopMost;
            for (int i = 0; i < characters.DropDownItems.Count; i++) ((ToolStripMenuItem)characters.DropDownItems[i]).Checked = selectedCharacter == i;
        };
        trayIcon = new NotifyIcon { Icon = applicationIcon, Text = "AI 사용량 모니터", ContextMenuStrip = trayMenu, Visible = true };
        trayIcon.DoubleClick += (_, _) => RestoreFromTray();
    }

    internal void HideToTray()
    {
        orangeTimer.Stop();
        orangeClock.Stop();
        Hide();
    }

    internal async void RestoreFromTray()
    {
        if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
        Show(); Activate(); BringToFront();
        if (!liveAnimation) return;
        orangeClock.Start(); orangeTimer.Start();
        await Task.WhenAll(codex.TickAsync(true), claude.TickAsync(true));
    }

    internal void PreviewOrange(double fraction, double seconds)
    {
        orangeDistance = fraction * orange.Perimeter(DesignSize);
        orangeTime = seconds;
        Invalidate();
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        if (!liveAnimation || orangeTimer is null || !Visible) return;
        if (WindowState == FormWindowState.Minimized) HideToTray();
        else { orangeClock.Start(); orangeTimer.Start(); }
    }

    internal void TogglePin()
    {
        TopMost = !TopMost;
        hoverPart = -1;
        Invalidate();
    }

    internal static Rectangle FitBounds(Rectangle proposed, Rectangle area, Size design, int edge = 0)
    {
        double maxScale = Math.Min(area.Width / (double)design.Width, area.Height / (double)design.Height);
        double scale = edge is 3 or 6 ? proposed.Height / (double)design.Height : proposed.Width / (double)design.Width;
        scale = Math.Clamp(scale, Math.Min(0.65, maxScale), maxScale);
        int width = Math.Max(1, Math.Min(area.Width, (int)Math.Round(design.Width * scale)));
        int height = Math.Max(1, Math.Min(area.Height, (int)Math.Round(design.Height * scale)));
        int x = edge is 1 or 4 or 7 ? proposed.Right - width : proposed.Left;
        int y = edge is 3 or 4 or 5 ? proposed.Bottom - height : proposed.Top;
        x = Math.Clamp(x, area.Left, area.Right - width);
        y = Math.Clamp(y, area.Top, area.Bottom - height);
        return new Rectangle(x, y, width, height);
    }

    protected override void SetBoundsCore(int x, int y, int width, int height, BoundsSpecified specified)
    {
        if (width > 0 && height > 0 && WindowState == FormWindowState.Normal)
        {
            var proposed = new Rectangle(x, y, width, height);
            var fitted = DisplayLimits.Fit(proposed, WorkAreas, DesignSize);
            x = fitted.X; y = fitted.Y; width = fitted.Width; height = fitted.Height;
            specified = BoundsSpecified.All;
        }
        base.SetBoundsCore(x, y, width, height, specified);
    }

    protected override void SetClientSizeCore(int width, int height)
    {
        if (width > 0 && height > 0 && WindowState == FormWindowState.Normal)
        {
            var proposed = new Rectangle(Left, Top, width, height);
            var fitted = DisplayLimits.Fit(proposed, WorkAreas, DesignSize);
            width = fitted.Width;
            height = fitted.Height;
        }
        base.SetClientSizeCore(width, height);
    }

    protected override void WndProc(ref Message message)
    {
        if (message.Msg is 0x214 or 0x216) // WM_SIZING / WM_MOVING
        {
            var proposed = Marshal.PtrToStructure<NativeRect>(message.LParam).Rectangle;
            var fitted = DisplayLimits.Fit(proposed, WorkAreas, DesignSize,
                message.Msg == 0x214 ? message.WParam.ToInt32() : 0);
            Marshal.StructureToPtr(new NativeRect(fitted), message.LParam, false);
            message.Result = (IntPtr)1;
            return;
        }
        base.WndProc(ref message);
        if (message.Msg == 0x84 && WindowState == FormWindowState.Normal) // WM_NCHITTEST
        {
            long packed = message.LParam.ToInt64();
            var point = PointToClient(new Point(unchecked((short)(packed & 0xffff)), unchecked((short)((packed >> 16) & 0xffff))));
            const int grip = 4;
            bool left = point.X < grip, right = point.X >= ClientSize.Width - grip;
            bool top = point.Y < grip, bottom = point.Y >= ClientSize.Height - grip;
            int hit = top && left ? 13 : top && right ? 14 : bottom && left ? 16 : bottom && right ? 17 :
                left ? 10 : right ? 11 : top ? 12 : bottom ? 15 : 1;
            message.Result = (IntPtr)hit;
        }
        else if (message.Msg == 0x24) // WM_GETMINMAXINFO
        {
            var info = Marshal.PtrToStructure<MinMaxInfo>(message.LParam);
            var area = DisplayLimits.Envelope(WorkAreas);
            var design = DesignSize;
            double maxScale = Math.Min(area.Width / (double)design.Width, area.Height / (double)design.Height);
            double minScale = Math.Min(0.65, maxScale);
            info.MinTrackSize = new NativePoint { X = (int)Math.Round(design.Width * minScale), Y = (int)Math.Round(design.Height * minScale) };
            info.MaxTrackSize = new NativePoint { X = (int)Math.Round(design.Width * maxScale), Y = (int)Math.Round(design.Height * maxScale) };
            Marshal.StructureToPtr(info, message.LParam, false);
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var g = e.Graphics;
        var size = DesignSize;
        g.ScaleTransform(ClientSize.Width / (float)size.Width, ClientSize.Height / (float)size.Height);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
        using var border = new Pen(Color.FromArgb(80, 93, 107));
        g.DrawRectangle(border, 0, 0, size.Width - 1, size.Height - 1);
        if (vertical) g.DrawLine(border, 10, RowHeight, ServiceWidth - 8, RowHeight);
        else g.DrawLine(border, ServiceWidth, 8, ServiceWidth, RowHeight - 8);
        DrawService(g, codex, 0, 0, RemainingColor(100, false), "Codex");
        DrawService(g, claude, vertical ? 0 : ServiceWidth, vertical ? RowHeight : 0, RemainingColor(100, true), "Claude");
        DrawButton(g, 0, "핀", TopMost);
        DrawButton(g, 1, vertical ? "↔" : "↕", vertical);
        DrawButton(g, 2, "＋", false);
        DrawButton(g, 3, "×", false);
        DrawButton(g, 4, "−", false);
        DrawButton(g, 5, "−", false);
        DrawButton(g, 6, "캐", false);
        orange.Draw(g, size, orangeTime, orangeDistance);
    }

    private RectangleF ButtonBounds(int button)
    {
        int toolsX = DesignSize.Width - ToolsWidth;
        return new RectangleF(toolsX + 5 + (button == 6 ? 3 : button % 3) * 22, button < 3 || button == 6 ? 5 : 27, 18, 18);
    }

    private void DrawButton(Graphics g, int button, string label, bool active)
    {
        var area = ButtonBounds(button);
        using var background = new SolidBrush(active ? Color.FromArgb(51, 81, 72) : Color.FromArgb(36, 43, 51));
        using var outline = new Pen(button is 2 or 5 ? Color.FromArgb(166, 119, 81) : active ? Color.FromArgb(112, 181, 156) : Color.FromArgb(73, 88, 102));
        g.FillRectangle(background, area);
        g.DrawRectangle(outline, area.X, area.Y, area.Width, area.Height);
        DrawText(g, label, buttonFont, active ? Color.FromArgb(160, 204, 184) : LabelColor, area, true);
    }

    private static Icon LoadApplicationIcon()
    {
        using var stream = typeof(MonitorForm).Assembly.GetManifestResourceStream("AppIcon.ico")!;
        using var icon = new Icon(stream, 32, 32);
        return (Icon)icon.Clone();
    }

    private static Bitmap LoadLogo(string resource)
    {
        using var stream = typeof(MonitorForm).Assembly.GetManifestResourceStream(resource)!;
        using var image = Image.FromStream(stream);
        return new Bitmap(image);
    }

    private void DrawService(Graphics g, UsageSource source, int x, int y, Color accent, string name)
    {
        var saved = g.Save();
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.DrawImage(source.Claude ? claudeLogo : gptLogo, new Rectangle(x + 25, y + 4, 22, 22));
        g.Restore(saved);
        DrawText(g, name, brandFont, accent, new RectangleF(x + 8, y + 27, 61, 19), true);
        bool outdated = source.Failed || source.Data is null || DateTimeOffset.UtcNow - source.Data.Retrieved > TimeSpan.FromMinutes(2);
        using var dot = new SolidBrush(outdated ? Color.FromArgb(211, 157, 92) : accent);
        g.FillEllipse(dot, x + 70, y + 34, 4, 4);
        DrawQuota(g, source.Data?.FiveHour, x + 79, y, "5시간", source.Claude, outdated);
        DrawQuota(g, source.Data?.Weekly, x + 289, y, "주간", source.Claude, outdated);
    }

    internal static Color RemainingColor(double remaining, bool isClaude)
    {
        if (remaining <= 20) return Color.FromArgb(255, 112, 124);
        if (isClaude)
            return remaining >= 80 ? Color.FromArgb(255, 180, 116) :
                remaining >= 60 ? Color.FromArgb(248, 205, 139) :
                remaining >= 40 ? Color.FromArgb(255, 174, 153) : Color.FromArgb(247, 145, 189);
        return remaining >= 80 ? Color.FromArgb(81, 221, 190) :
            remaining >= 60 ? Color.FromArgb(149, 224, 121) :
            remaining >= 40 ? Color.FromArgb(244, 215, 102) : Color.FromArgb(255, 169, 96);
    }

    private void DrawQuota(Graphics g, Quota? quota, int x, int y, string title, bool isClaude, bool outdated)
    {
        const int width = 200;
        bool valid = quota is not null && !quota.Expired;
        DrawText(g, title, labelFont, LabelColor, new RectangleF(x, y + 7, 40, 19));
        var remaining = valid ? $"{quota!.Remaining:0.#}% 남음" : quota?.Expired == true ? "확인 중" : "—";
        var color = valid && !outdated ? RemainingColor(quota!.Remaining, isClaude) : Muted;
        DrawText(g, remaining, valueFont, color, new RectangleF(x + 43, y + 7, 74, 19));
        DrawText(g, Countdown(quota?.Reset), timeFont, TimeColor, new RectangleF(x + 120, y + 7, 84, 19));
        using var track = new SolidBrush(Color.FromArgb(12, 16, 22));
        using var outline = new Pen(Color.FromArgb(151, 166, 185));
        g.FillRectangle(track, x, y + 31, width, 7);
        if (valid)
        {
            using var fill = new SolidBrush(color);
            g.FillRectangle(fill, x + 1, y + 32, (float)((width - 2) * quota!.Remaining / 100), 5);
        }
        g.DrawRectangle(outline, x + 0.5f, y + 31.5f, width - 1, 6);
    }

    internal static string Countdown(DateTimeOffset? reset)
    {
        if (reset is null) return "";
        var left = reset.Value - DateTimeOffset.UtcNow;
        if (left <= TimeSpan.Zero) return "초기화 여부 확인 중";
        if (left.TotalDays >= 1) return $"{(int)left.TotalDays}일 {left.Hours}시간 후";
        if (left.TotalHours >= 1) return $"{(int)left.TotalHours}시간 {left.Minutes}분 후";
        return $"{Math.Max(1, (int)Math.Ceiling(left.TotalMinutes))}분 후";
    }

    private static void DrawText(Graphics g, string text, Font font, Color color, RectangleF area, bool centered = false)
    {
        using var brush = new SolidBrush(color);
        using var format = new StringFormat { Alignment = centered ? StringAlignment.Center : StringAlignment.Near,
            LineAlignment = StringAlignment.Center, Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap };
        g.DrawString(text, font, brush, area, format);
    }

    private PointF LogicalPoint(Point point) => new(point.X * DesignSize.Width / (float)ClientSize.Width,
        point.Y * DesignSize.Height / (float)ClientSize.Height);

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var point = LogicalPoint(e.Location);
        int button = -1;
        for (int i = 0; i < 7; i++) if (ButtonBounds(i).Contains(point)) { button = i; break; }
        int service = vertical ? (point.Y < RowHeight ? 0 : 1) : (point.X < ServiceWidth ? 0 : 1);
        int part = button >= 0 ? button + 2 : service;
        Cursor = button >= 0 ? Cursors.Hand : Cursors.SizeAll;
        if (part == hoverPart) return;
        hoverPart = part;
        if (button >= 0)
        {
            tooltip.SetToolTip(this, button switch { 0 => TopMost ? "최상단 고정 해제" : "최상단 고정", 1 => vertical ? "가로 모드로 전환" : "세로 모드로 전환", 2 => "캐릭터 크기 키우기", 3 => "종료", 4 => "트레이로 숨기기", 5 => "캐릭터 크기 줄이기", _ => "캐릭터 선택 · " + OrangeWalker.Characters[selectedCharacter].Name });
            return;
        }
        var source = service == 0 ? codex : claude;
        string details = (source.Claude ? "Claude Code" : "Codex") + " · " + source.Status;
        if (source.Data is { } data)
        {
            details += "\n마지막 확인: " + TimeZoneInfo.ConvertTime(data.Retrieved, Korea).ToString("yyyy-MM-dd HH:mm:ss") + " (한국 시간)";
            details += Details("5시간", data.FiveHour) + Details("주간", data.Weekly);
        }
        details += "\n빈 곳 드래그: 이동 · 테두리 드래그: 비율 유지 크기 조절";
        tooltip.SetToolTip(this, details);
    }

    private static string Details(string name, Quota? quota)
    {
        if (quota is null) return "\n" + name + ": 정보 없음";
        var reset = quota.Reset is { } date ? TimeZoneInfo.ConvertTime(date, Korea).ToString("yyyy-MM-dd HH:mm:ss") : "미제공";
        return $"\n{name}: 사용 {quota.Used:0.#}% / 남음 {quota.Remaining:0.#}% · 초기화 {reset}";
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left) return;
        var point = LogicalPoint(e.Location);
        for (int i = 0; i < 7; i++)
        {
            if (!ButtonBounds(i).Contains(point)) continue;
            switch (i) { case 0: TogglePin(); break; case 1: ToggleLayout(); break; case 2: ChangeOrangeSize(4); break; case 3: Close(); break; case 4: HideToTray(); break; case 5: ChangeOrangeSize(-4); break; case 6: characterMenu.Show(this, new Point(e.X, e.Y)); break; }
            return;
        }
        ReleaseCapture();
        SendMessage(Handle, 0xA1, (IntPtr)2, IntPtr.Zero);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            timer.Dispose(); tooltip.Dispose();
            orangeTimer.Dispose(); orangeClock.Stop(); orange.Dispose();
            trayIcon?.Dispose(); trayMenu?.Dispose(); characterMenu.Dispose();
            foreach (var icon in characterIcons) icon.Dispose();
            codex.Changed -= RefreshDisplay; claude.Changed -= RefreshDisplay;
            applicationIcon.Dispose(); gptLogo.Dispose(); claudeLogo.Dispose();
            labelFont.Dispose(); valueFont.Dispose(); timeFont.Dispose(); brandFont.Dispose(); buttonFont.Dispose();
        }
        base.Dispose(disposing);
    }
}
