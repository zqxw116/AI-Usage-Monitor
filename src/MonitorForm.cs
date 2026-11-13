using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Diagnostics;
using DisplayLimits = UsageMonitor.DesktopBounds;

namespace UsageMonitor;

internal sealed class MonitorForm : Form
{
    private const int ServiceWidth = 500;
    private const int RowHeight = 50;
    private const int ExpandedToolsWidth = 94;
    private const int MenuWidth = 30;
    private static readonly Color Background = Color.FromArgb(32, 38, 46);
    private static readonly Color LabelColor = Color.FromArgb(220, 228, 236);
    private static readonly Color TimeColor = Color.FromArgb(207, 217, 228);
    private static readonly Color Muted = Color.FromArgb(149, 161, 174);
    private static readonly TimeZoneInfo Korea = TimeZoneInfo.FindSystemTimeZoneById("Korea Standard Time");
    private readonly Icon applicationIcon = LoadApplicationIcon();
    private readonly Bitmap gptLogo = LoadLogo("GptLogo.png");
    private readonly Bitmap claudeLogo = LoadLogo("ClaudeLogo.png");
    private readonly Bitmap geminiLogo = LoadLogo("GeminiLogo.png");
    private readonly UsageSource[] sources;
    private readonly MonitorSettings settings;
    private ConnectionForm? connections;
    private readonly System.Windows.Forms.Timer timer = new() { Interval = 1000 };
    private readonly System.Windows.Forms.Timer orangeTimer = new() { Interval = 33 };
    private OrangeWalker orange = new(OrangeWalker.Characters[0]);
    private readonly ContextMenuStrip characterMenu = new() { ImageScalingSize = new Size(24, 24) };
    private readonly List<Image> characterIcons = new();
    private int selectedCharacter;
    private NotifyIcon? trayIcon;
    private ContextMenuStrip? trayMenu;
    private readonly Stopwatch orangeClock = new();
    private bool interactiveMove;
    private readonly bool liveAnimation;
    private double orangeDistance, orangeTime;
    private readonly (double Period, double Offset)[] iconMotion = Enumerable.Range(0, 3)
        .Select(i => (3.4 + Random.Shared.NextDouble() * .6, i * .75 + Random.Shared.NextDouble() * .35)).ToArray();
    private double EffectTime => orangeTime;
    private readonly Font planFont = new("Segoe UI", 10, FontStyle.Bold, GraphicsUnit.Pixel);
    private readonly ToolTip tooltip = new() { AutoPopDelay = 20000, InitialDelay = 300, ReshowDelay = 100 };
    // 픽셀 글꼴과 단일 배율로 모든 요소를 함께 확대·축소합니다.
    private readonly Font labelFont = new("맑은 고딕", 11.5f, FontStyle.Bold, GraphicsUnit.Pixel);
    private readonly Font valueFont = new("맑은 고딕", 12.5f, FontStyle.Bold, GraphicsUnit.Pixel);
    private readonly Font timeFont = new("맑은 고딕", 12f, FontStyle.Regular, GraphicsUnit.Pixel);
    private readonly Font brandFont = new("Segoe UI", 11f, FontStyle.Bold, GraphicsUnit.Pixel);
    private readonly Font buttonFont = new("맑은 고딕", 12f, FontStyle.Regular, GraphicsUnit.Pixel);
    private bool vertical;
    private bool toolsExpanded;
    private Size displayedDesign;
    private int hoverPart = -1;
    private int EnabledCount => sources is null ? 2 : sources.Count(source => source.Enabled);
    private bool compactCodex;
    private int PanelWidth(UsageSource s) => s.Kind == ServiceKind.Codex && compactCodex ? ServiceWidth - 210 : ServiceWidth;
    private int ServiceAreaWidth => sources is null ? ServiceWidth * 2 : EnabledCount == 0 ? 260 :
        vertical ? ActiveSources.Max(PanelWidth) : ActiveSources.Sum(PanelWidth);
    private IEnumerable<(UsageSource Source, Rectangle Bounds)> Panels()
    {
        int x = 0, y = 0;
        foreach (var s in ActiveSources)
        {
            int width = PanelWidth(s);
            yield return (s, new Rectangle(x, y, width, RowHeight));
            if (vertical) y += RowHeight; else x += width;
        }
    }
    private int ToolsWidth => MenuWidth + (toolsExpanded ? ExpandedToolsWidth : 0);
    internal bool ToolsExpanded => toolsExpanded;
    internal Size DesignSize => new(ServiceAreaWidth + ToolsWidth, RowHeight * (vertical ? Math.Max(1, EnabledCount) : 1));
    private IEnumerable<UsageSource> ActiveSources
    {
        get { return sources.Where(source => source.Enabled); }
    }
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

    public MonitorForm(UsageSource codex, UsageSource claude, bool live = true, MonitorSettings? settings = null, UsageSource? gemini = null)
    {
        sources = gemini is null ? new[] { codex, claude } : new[] { codex, claude, gemini };
        this.settings = settings ?? (live ? MonitorSettings.Load() : new MonitorSettings());
        foreach (var source in sources) source.SetEnabled(this.settings.IsEnabled(source.Kind));
        liveAnimation = live;
        UpdateCodexWidth();
        Text = "AI 사용량 모니터";
        AutoScaleMode = AutoScaleMode.None;
        FormBorderStyle = FormBorderStyle.None;
        MaximizeBox = false;
        displayedDesign = DesignSize;
        ClientSize = displayedDesign;
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Background;
        DoubleBuffered = true;
        ShowInTaskbar = false;
        TopMost = true;
        Icon = applicationIcon;
        AccessibleName = "AI 서비스 사용량 모니터";
        foreach (var source in sources) source.Changed += RefreshDisplay;
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
            if (this.settings.VisualEffects)
            {
                foreach (var panel in Panels())
                {
                    var box = panel.Bounds;
                    Invalidate(Rectangle.Ceiling(new RectangleF((box.X + 18) * sx, (box.Y + 3) * sy, 36 * sx, 29 * sy)));
                    if (panel.Source.Data is not null)
                        Invalidate(Rectangle.Ceiling(new RectangleF((box.X + 79) * sx, (box.Y + 30) * sy, (box.Width - 88) * sx, 12 * sy)));
                }
            }
        };
        timer.Tick += async (_, _) =>
        {
            if (Environment.TickCount64 >= nextUpdateCheck) ScheduleUpdateCheck();
            if (!interactiveMove) Invalidate();
            await Task.WhenAll(sources.Select(source => source.TickAsync()));
        };
        if (live) Shown += async (_, _) =>
        {
            timer.Start();
            orangeClock.Start();
            orangeTimer.Start();
            ScheduleUpdateCheck();
            await Task.WhenAll(sources.Select(source => source.TickAsync(true)));
        };
    }

    private void RefreshDisplay()
    {
        if (IsDisposed) return;
        UpdateCodexWidth();
        RefreshLayout();
        Invalidate();
        hoverPart = -1;
    }

    private void UpdateCodexWidth()
    {
        var codex = sources[0];
        if (codex.Data is { } d && !codex.Failed)
            compactCodex = d.CodexPro && d.FiveHour is null && d.Weekly is not null;
    }

    internal void ToggleLayout()
    {
        vertical = !vertical;
        RefreshLayout();
    }

    internal void ToggleTools()
    {
        toolsExpanded = !toolsExpanded;
        RefreshLayout();
    }

    private void RefreshLayout()
    {
        var size = DesignSize;
        if (size == displayedDesign) return;
        double scale = displayedDesign.Width > 0 ? ClientSize.Width / (double)displayedDesign.Width : 1;
        double fraction = displayedDesign.Width > 0 ? orangeDistance / orange.Perimeter(displayedDesign) : 0;
        displayedDesign = size;
        SetBounds(Left, Top, (int)Math.Round(size.Width * scale), (int)Math.Round(size.Height * scale));
        orangeDistance = fraction * orange.Perimeter(size);
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
        var effects = new ToolStripMenuItem("아이콘·바 애니메이션") { Checked = settings.VisualEffects };
        effects.Click += (_, _) =>
        {
            settings.VisualEffects = !settings.VisualEffects;
            try { settings.Save(); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                settings.VisualEffects = !settings.VisualEffects;
                MessageBox.Show(this, "애니메이션 설정을 저장하지 못했습니다.");
            }
            effects.Checked = settings.VisualEffects;
            Invalidate();
        };
        trayMenu.Items.Add(effects);
        var serviceItems = new List<(UsageSource Source, ToolStripMenuItem Item)>();
        foreach (var source in sources)
        {
            var item = new ToolStripMenuItem(source.Name + (source.Gemini ? " 활성화 · 실험적" : " 활성화")) { Checked = source.Enabled };
            item.Click += (_, _) => SetServiceEnabled(source, !source.Enabled);
            trayMenu.Items.Add(item);
            serviceItems.Add((source, item));
        }
        trayMenu.Items.Add("서비스 연결 관리…", null, (_, _) => ShowConnections());
        trayMenu.Items.Add("최신 버전 업데이트…", null, async (_, _) => await UpdateAppAsync());
        trayMenu.Items.Add(new ToolStripSeparator());
        trayMenu.Items.Add("종료", null, (_, _) => Close());
        trayMenu.Opening += (_, _) =>
        {
            pin.Checked = TopMost;
            foreach (var service in serviceItems) service.Item.Checked = service.Source.Enabled;
            for (int i = 0; i < characters.DropDownItems.Count; i++) ((ToolStripMenuItem)characters.DropDownItems[i]).Checked = selectedCharacter == i;
        };
        trayIcon = new NotifyIcon { Icon = applicationIcon, Text = "AI 사용량 모니터", ContextMenuStrip = trayMenu, Visible = true };
        trayIcon.DoubleClick += (_, _) => RestoreFromTray();
    }

    private bool updatingApp;
    private long nextUpdateCheck = long.MaxValue;
    private readonly CancellationTokenSource updateLifetime = new();

    private void ScheduleUpdateCheck()
    {
        nextUpdateCheck = Environment.TickCount64 + (long)TimeSpan.FromMinutes(10).TotalMilliseconds;
        _ = UpdateAppAsync(automatic: true);
    }

    private async Task UpdateAppAsync(bool automatic = false)
    {
        if (updatingApp) return;
        updatingApp = true;
        if (connections is { IsDisposed: false }) connections.SetUpdateStatus("확인 중…");
        try
        {
            var token = updateLifetime.Token;
            var release = await AppUpdater.CheckAsync(token);
            token.ThrowIfCancellationRequested();
            Form owner = connections is { IsDisposed: false, Visible: true } ? connections : this;
            if (release is null)
            {
                if (!automatic) MessageBox.Show(owner, $"현재 버전 {AppUpdater.CurrentVersion}입니다. 설치할 새 릴리스가 없습니다.", "업데이트");
                return;
            }
            if (!automatic && MessageBox.Show(owner, $"{release.Version} 버전을 설치하고 모니터를 다시 시작할까요?",
                "업데이트", MessageBoxButtons.YesNo) != DialogResult.Yes) return;
            if (connections is { IsDisposed: false }) connections.SetUpdateStatus("다운로드 중…");
            if (trayIcon is not null) trayIcon.Text = "AI 사용량 모니터 · 업데이트 다운로드 중";
            await AppUpdater.InstallAsync(release, token);
            Application.Exit();
        }
        catch (Exception ex)
        {
            if (!automatic && !IsDisposed && !Disposing) MessageBox.Show(this, ex.Message, "업데이트 실패", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        finally
        {
            updatingApp = false;
            if (connections is { IsDisposed: false }) connections.SetUpdateStatus(null);
            if (!IsDisposed && trayIcon is not null) trayIcon.Text = "AI 사용량 모니터";
        }
    }

    internal void SetServiceEnabled(UsageSource source, bool enabled)
    {
        bool previous = source.Enabled;
        settings.SetEnabled(source.Kind, enabled);
        try { if (liveAnimation) settings.Save(); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            settings.SetEnabled(source.Kind, previous);
            MessageBox.Show(this, "활성화 설정을 저장하지 못했습니다. 저장 폴더 권한을 확인해 주세요.", "설정 저장 실패", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        source.SetEnabled(enabled);
        if (enabled && liveAnimation) _ = source.TickAsync(true);
    }

    internal void ShowConnections()
    {
        if (connections is { IsDisposed: false }) { connections.Activate(); return; }
        connections = new ConnectionForm(sources, SetServiceEnabled) { TopMost = TopMost };
        connections.UpdateRequested += async (_, _) => await UpdateAppAsync();
        connections.Show(this);
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
        await Task.WhenAll(sources.Select(source => source.TickAsync(true)));
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
        if (!liveAnimation || orangeTimer is null || !Visible || interactiveMove) return;
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
        if (message.Msg == 0x231) // WM_ENTERSIZEMOVE
        {
            interactiveMove = true;
            orangeTimer.Stop();
            orangeClock.Stop();
        }
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
        if (message.Msg == 0x232) // WM_EXITSIZEMOVE, including a cancelled drag
        {
            interactiveMove = false;
            if (liveAnimation && Visible && WindowState == FormWindowState.Normal)
            {
                orangeClock.Start();
                orangeTimer.Start();
            }
            Invalidate();
        }
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
        using var divider = new Pen(Color.FromArgb(80, 93, 107));
        foreach (var (source, bounds) in Panels())
        {
            if (bounds.X != 0 || bounds.Y != 0)
            {
                if (vertical) g.DrawLine(divider, 10, bounds.Y, ServiceAreaWidth - 8, bounds.Y);
                else g.DrawLine(divider, bounds.X, 8, bounds.X, RowHeight - 8);
            }
            DrawService(g, source, bounds.X, bounds.Y,
                RemainingColor(100, source.Kind), source.Name);
        }
        if (EnabledCount == 0)
            DrawText(g, "메뉴에서 서비스를 선택하세요", valueFont, LabelColor, new RectangleF(10, 10, ServiceAreaWidth - 20, 30), true);
        orange.Draw(g, size, orangeTime, orangeDistance);
        if (toolsExpanded)
        {
            DrawButton(g, 0, "", TopMost);
            DrawButton(g, 1, vertical ? "↔" : "↕", vertical);
            DrawButton(g, 2, "＋", false);
            DrawButton(g, 3, "×", false);
            DrawButton(g, 4, "−", false);
            DrawButton(g, 5, "−", false);
            DrawButton(g, 6, "", false);
            DrawButton(g, 7, "", connections is { IsDisposed: false });
        }
        DrawButton(g, 8, "", toolsExpanded);
        var menu = ButtonBounds(8);
        using (var icon = new Pen(LabelColor, 1.5f))
        {
            if (toolsExpanded)
            {
                g.DrawLine(icon, menu.X + 8, menu.Y + 14, menu.X + 14, menu.Y + 20);
                g.DrawLine(icon, menu.X + 14, menu.Y + 20, menu.X + 8, menu.Y + 26);
            }
            else for (int line = 0; line < 3; line++) g.DrawLine(icon, menu.X + 6, menu.Y + 14 + line * 5, menu.X + 16, menu.Y + 14 + line * 5);
        }
        using var border = new Pen(Color.White, 3);
        g.DrawRectangle(border, 1.5f, 1.5f, size.Width - 3, size.Height - 3);
    }

    internal RectangleF ButtonBounds(int button)
    {
        if (button == 8) return new RectangleF(DesignSize.Width - MenuWidth + 4, 5, 22, 40);
        int toolsX = DesignSize.Width - ToolsWidth;
        return new RectangleF(toolsX + 5 + (button >= 6 ? 3 : button % 3) * 22, button < 3 || button == 6 ? 5 : 27, 18, 18);
    }

    private void DrawButton(Graphics g, int button, string label, bool active)
    {
        var area = ButtonBounds(button);
        using var background = new SolidBrush(active ? Color.FromArgb(51, 81, 72) : Color.FromArgb(36, 43, 51));
        using var outline = new Pen(button is 2 or 5 ? Color.FromArgb(166, 119, 81) : active ? Color.FromArgb(112, 181, 156) : Color.FromArgb(73, 88, 102));
        g.FillRectangle(background, area);
        g.DrawRectangle(outline, area.X, area.Y, area.Width, area.Height);
        Color ink = active ? Color.FromArgb(160, 224, 194) : LabelColor;
        if (button is not (0 or 6 or 7))
        {
            if (label.Length > 0) DrawText(g, label, buttonFont, ink, area, true);
            return;
        }
        var saved = g.Save();
        g.TranslateTransform(area.X + 9, area.Y + 9);
        using var pen = new Pen(ink, 1.5f) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
        if (button == 0)
        {
            g.RotateTransform(30);
            g.DrawPolygon(pen, new PointF[] { new(-3,-5), new(3,-5), new(2,-1), new(4,2), new(-4,2), new(-2,-1) });
            g.DrawLine(pen, 0, 2, 0, 6);
        }
        else if (button == 6)
        {
            using var paw = new SolidBrush(ink);
            g.FillEllipse(paw, -3.6f, -.5f, 7.2f, 5.7f);
            g.FillEllipse(paw, -5.6f, -3, 2.8f, 3.6f);
            g.FillEllipse(paw, -1.5f, -5.7f, 3, 4);
            g.FillEllipse(paw, 2.8f, -3, 2.8f, 3.6f);
        }
        else
        {
            g.RotateTransform(-45);
            g.DrawEllipse(pen, -6, -2.5f, 7, 5);
            g.DrawEllipse(pen, -1, -2.5f, 7, 5);
        }
        g.Restore(saved);
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
        return new Bitmap(image, 64, 64);
    }

    private void DrawService(Graphics g, UsageSource source, int x, int y, Color accent, string name)
    {
        var saved = g.Save();
        g.InterpolationMode = InterpolationMode.Bilinear;
        var motion = iconMotion[(int)source.Kind];
        double cycle = (EffectTime + motion.Offset) / motion.Period;
        double t = Math.Min(1, (cycle - Math.Floor(cycle)) / .82);
        g.TranslateTransform(x + 36, y + 17);
        if (settings.VisualEffects)
        {
            float pulse = (float)Math.Sin(t * Math.PI);
            g.RotateTransform((float)(360 * t * t * (3 - 2 * t)) * ((long)cycle % 2 == 0 ? 1 : -1));
            float scale = 1 - .45f * pulse;
            g.ScaleTransform(scale, scale);
        }
        g.DrawImage(source.Gemini ? geminiLogo : source.Claude ? claudeLogo : gptLogo, new Rectangle(-13, -13, 26, 26));
        g.Restore(saved);
        string? plan = source.Data?.PlanLabel;
        DrawText(g, name, brandFont, accent, new RectangleF(x + 3, y + (plan is null ? 31 : 28), 66, 13), true);
        if (plan is not null) DrawText(g, source.Gemini ? "CLI · " + plan : plan, planFont, TimeColor, new RectangleF(x + 2, y + 38, 68, 10), true);
        if (!source.Enabled || source.Connecting)
        {
            DrawText(g, source.Enabled ? "브라우저 로그인 대기 중" : "비활성 · 연결 관리에서 켜기", valueFont,
                source.Enabled ? LabelColor : Muted, new RectangleF(x + 79, y + 10, 400, 30));
            return;
        }
        bool outdated = source.Failed || source.Data is null || DateTimeOffset.UtcNow - source.Data.Retrieved > TimeSpan.FromMinutes(2);
        using var dot = new SolidBrush(outdated ? Color.FromArgb(211, 157, 92) : accent);
        g.FillEllipse(dot, x + 70, y + 34, 4, 4);
        bool pro = source.Kind == ServiceKind.Codex && source.Data?.CodexPro == true;
        bool weeklyOnly = PanelWidth(source) < ServiceWidth;
        if (!weeklyOnly)
            DrawQuota(g, source.Data?.FiveHour, x + 79, y, source.Data?.PrimaryLabel ?? (source.Gemini ? "모델1" : "5시간"), source.Kind, outdated);
        DrawQuota(g, source.Data?.Weekly, x + (weeklyOnly ? 79 : 289), y,
            source.Data?.SecondaryLabel ?? (source.Gemini ? "모델2" : "주간"),
            source.Kind, outdated, 200, pro);
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

    internal void DrawQuota(Graphics g, Quota? quota, int x, int y, string title, bool isClaude, bool outdated)
        => DrawQuota(g, quota, x, y, title, isClaude ? ServiceKind.Claude : ServiceKind.Codex, outdated);

    private static Color RemainingColor(double remaining, ServiceKind kind)
    {
        if (kind != ServiceKind.Gemini) return RemainingColor(remaining, kind == ServiceKind.Claude);
        return remaining <= 20 ? Color.FromArgb(255, 112, 124) : remaining >= 80 ? Color.FromArgb(135, 190, 255) :
            remaining >= 60 ? Color.FromArgb(166, 181, 255) : remaining >= 40 ? Color.FromArgb(204, 174, 255) : Color.FromArgb(255, 184, 126);
    }

    private static Brush ProFill(float x, float y, float length, Color band, bool low)
    {
        var brush = new LinearGradientBrush(new RectangleF(x, y, Math.Max(1, length), 7), band, band, 0f);
        brush.InterpolationColors = new ColorBlend {
            Positions = [0, .22f, .48f, .73f, 1],
            Colors = low
                ? [band, Color.Salmon, Color.Tomato, Color.Crimson, band]
                : [band, Color.FromArgb(55, 225, 232), Color.FromArgb(108, 135, 255),
                    Color.FromArgb(207, 111, 255), Color.FromArgb(255, 131, 177)]
        };
        return brush;
    }

    private void DrawQuota(Graphics g, Quota? quota, int x, int y, string title, ServiceKind kind, bool outdated, int width = 200, bool pro = false)
    {
        bool valid = quota is not null && !quota.Expired;
        DrawText(g, title, labelFont, pro ? Color.FromArgb(238, 197, 255) : LabelColor, new RectangleF(x, y + 7, 36, 19));
        var remaining = valid ? $"{quota!.Remaining:0.#}% 남음" : quota?.Expired == true ? "확인 중" : "—";
        var color = valid ? RemainingColor(quota!.Remaining, kind) : Muted;
        DrawText(g, remaining, valueFont, color, new RectangleF(x + 36, y + 7, 71, 19));
        string countdown = Countdown(quota?.Reset);
        if (countdown.Length > 0 && g.IsVisible(new RectangleF(x + 107, y + 7, width - 107, 19)))
        {
            var state = g.Save();
            float measured = g.MeasureString(countdown, timeFont).Width + 2;
            g.TranslateTransform(x + 107, y + 7);
            g.ScaleTransform(Math.Min(1, (width - 107) / measured), 1);
            DrawText(g, countdown, timeFont, TimeColor, new RectangleF(0, 0, measured, 19));
            g.Restore(state);
        }
        using var track = new SolidBrush(Color.FromArgb(12, 16, 22));
        using var outline = new Pen(outdated ? Color.FromArgb(211, 157, 92) : Color.FromArgb(151, 166, 185));
        g.FillRectangle(track, x, y + 31, width, 9);
        if (valid)
        {
            float length = (float)((width - 2) * quota!.Remaining / 100);
            if (settings.VisualEffects && length > 1)
            {
                double t = EffectTime + (int)kind * .41;
                var state = g.Save();
                g.SetClip(new RectangleF(x + 1, y + 32, length, 7), CombineMode.Intersect);
                using Brush palette = pro ? ProFill(x + 1, y + 32, length, color, quota.Remaining <= 20) : new SolidBrush(color);
                if (palette is LinearGradientBrush rainbow) rainbow.WrapMode = WrapMode.TileFlipX;
                using var lightA = LightWave(64, .48f);
                using var lightB = LightWave(91, .54f);
                for (int row = 0; row < 7; row++)
                {
                    float v = row / 6f;
                    float bend = (float)Math.Sin(v * 3.5 + t * 1.7);
                    if (palette is LinearGradientBrush gradient)
                    {
                        gradient.ResetTransform();
                        gradient.TranslateTransform(bend * Math.Min(length * .14f, 17), 0);
                    }
                    float line = y + 32 + row;
                    g.FillRectangle(palette, x + 1, line, length, 1);
                    lightA.ResetTransform();
                    lightA.TranslateTransform(x + 1 + (float)(t * 28 % 64) + bend * 13, 0);
                    lightB.ResetTransform();
                    lightB.TranslateTransform(x + 1 + (float)(t * 19 % 91) + (float)Math.Sin(v * 4.1 - t * 1.1) * 18, 0);
                    g.FillRectangle(lightA, x + 1, line, length, 1);
                    g.FillRectangle(lightB, x + 1, line, length, 1);
                }
                g.Restore(state);
            }
            else
            {
                using Brush fill = pro ? ProFill(x + 1, y + 32, length, color, quota.Remaining <= 20) : new SolidBrush(color);
                g.FillRectangle(fill, x + 1, y + 32, length, 7);
            }
        }
        g.DrawRectangle(outline, x + 0.5f, y + 31.5f, width - 1, 8);
    }

    private static LinearGradientBrush LightWave(int period, float strength)
    {
        var brush = new LinearGradientBrush(new Rectangle(0, 0, period, 7), Color.FromArgb(0, Color.White),
            Color.FromArgb((int)(strength * 255), Color.White), 0f);
        brush.SetSigmaBellShape(.5f);
        return brush;
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
        if (!g.IsVisible(area)) return;
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
        for (int i = toolsExpanded ? 0 : 8; i < 9; i++) if (ButtonBounds(i).Contains(point)) { button = i; break; }
        var source = Panels().FirstOrDefault(p => p.Bounds.Contains((int)point.X, (int)point.Y)).Source;
        int service = source is null ? -1 : (int)source.Kind;
        int part = button >= 0 ? button + 3 : service;
        Cursor = button >= 0 ? Cursors.Hand : Cursors.SizeAll;
        if (part == hoverPart) return;
        hoverPart = part;
        if (button >= 0)
        {
            tooltip.SetToolTip(this, button switch { 0 => TopMost ? "최상단 고정 해제" : "최상단 고정", 1 => vertical ? "가로 모드로 전환" : "세로 모드로 전환", 2 => "캐릭터 크기 키우기", 3 => "종료", 4 => "트레이로 숨기기", 5 => "캐릭터 크기 줄이기", 6 => "캐릭터 선택 · " + OrangeWalker.Characters[selectedCharacter].Name, 7 => "서비스 활성화 · 브라우저 로그인", _ => toolsExpanded ? "버튼 목록 접기" : "버튼 목록 펼치기" });
            return;
        }
        if (source is null || point.X >= ServiceAreaWidth) { tooltip.SetToolTip(this, "메뉴에서 서비스 연결과 창 조작을 관리합니다."); return; }
        string details = source.Name + " · " + source.Status;
        if (source.Detail is not null) details += "\n" + source.Detail;
        if (source.Data is { } data)
        {
            details += "\n마지막 확인: " + TimeZoneInfo.ConvertTime(data.Retrieved, Korea).ToString("yyyy-MM-dd HH:mm:ss") + " (한국 시간)";
            details += (source.Gemini ? "\nCode Assist 등급: " : "\n요금제: ") + (data.PlanLabel ?? "미확인");
            if (PanelWidth(source) == ServiceWidth) details += Details(data.PrimaryLabel ?? "5시간", data.FiveHour);
            details += Details(data.SecondaryLabel ?? "주간", data.Weekly);
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
        if (e.Button == MouseButtons.Right) { ShowConnections(); return; }
        if (e.Button != MouseButtons.Left) return;
        var point = LogicalPoint(e.Location);
        for (int i = toolsExpanded ? 0 : 8; i < 9; i++)
        {
            if (!ButtonBounds(i).Contains(point)) continue;
            switch (i) { case 0: TogglePin(); break; case 1: ToggleLayout(); break; case 2: ChangeOrangeSize(4); break; case 3: Close(); break; case 4: HideToTray(); break; case 5: ChangeOrangeSize(-4); break; case 6: characterMenu.Show(this, new Point(e.X, e.Y)); break; case 7: ShowConnections(); break; case 8: ToggleTools(); break; }
            return;
        }
        var screen = PointToScreen(e.Location);
        var position = (IntPtr)unchecked((screen.Y << 16) | (screen.X & 0xffff));
        ReleaseCapture();
        SendMessage(Handle, 0xA1, (IntPtr)2, position);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            if (!IsDisposed) { updateLifetime.Cancel(); updateLifetime.Dispose(); }
            timer.Dispose(); tooltip.Dispose();
            connections?.Close();
            orangeTimer.Dispose(); orangeClock.Stop(); orange.Dispose();
            trayIcon?.Dispose(); trayMenu?.Dispose(); characterMenu.Dispose();
            foreach (var icon in characterIcons) icon.Dispose();
            foreach (var source in sources) source.Changed -= RefreshDisplay;
            applicationIcon.Dispose(); gptLogo.Dispose(); claudeLogo.Dispose(); geminiLogo.Dispose();
            labelFont.Dispose(); valueFont.Dispose(); timeFont.Dispose(); brandFont.Dispose(); buttonFont.Dispose(); planFont.Dispose();
        }
        base.Dispose(disposing);
    }
}
