using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace UsageMonitor;

internal sealed class ConnectionForm : Form
{
    private sealed class RefreshButton : Button
    {
        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            float x = Width / 2f, y = Height / 2f;
            using var pen = new Pen(Enabled ? Color.FromArgb(230, 235, 241) : Color.FromArgb(144, 157, 170), 1.8f)
                { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
            g.DrawArc(pen, x - 8, y - 8, 16, 16, 45, 285);
            g.DrawLines(pen, new[] { new PointF(x + 7, y - 10), new PointF(x + 7, y - 4), new PointF(x + 1, y - 4) });
        }
    }
    private sealed class ConnectButton : Button
    {
        protected override void OnPaint(PaintEventArgs e)
        {
            if (Enabled) { base.OnPaint(e); return; }
            e.Graphics.Clear(BackColor);
            using var border = new Pen(Color.FromArgb(80, 80, 80));
            e.Graphics.DrawRectangle(border, 0, 0, Width - 1, Height - 1);
            TextRenderer.DrawText(e.Graphics, Text, Font, ClientRectangle, Color.FromArgb(180, 180, 180),
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }
    }

    private readonly Font bodyFont = new("맑은 고딕", 10, FontStyle.Regular);
    private readonly UsageSource[] sources;
    private readonly CheckBox[] toggles;
    private readonly Label[] statuses;
    private readonly Button[] connectButtons;
    private readonly Button[] refreshButtons;
    private readonly TableLayoutPanel layout;
    private readonly RowStyle noticeRow = new(SizeType.Absolute, 0);
    private readonly Panel noticePanel = new() { Dock = DockStyle.Fill, Padding = new Padding(10), BackColor = Color.FromArgb(30, 30, 30), Visible = false };
    private readonly Label noticeTitle = new() { Dock = DockStyle.Top, Height = 24 };
    private readonly RichTextBox noticeContent = new()
    {
        Dock = DockStyle.Fill, ReadOnly = true, DetectUrls = false, BorderStyle = BorderStyle.None,
        ScrollBars = RichTextBoxScrollBars.Vertical, BackColor = Color.FromArgb(30, 30, 30), ForeColor = Color.FromArgb(224, 224, 224)
    };
    private readonly Action<UsageSource, bool> setEnabled;
    private readonly Label outcome = new() { AutoSize = false, Dock = DockStyle.Fill };
    private readonly LinkLabel reopen = new() { Text = "로그인 브라우저 다시 열기", AutoSize = true, Visible = false };
    private readonly Button cancel = new() { Text = "로그인 취소", AutoSize = true, Visible = false };
    private CancellationTokenSource? loginCancellation;
    private OfficialLogin? login;
    private Uri? loginUrl;
    private UsageSource? connectingSource;
    private bool updating;
    internal event EventHandler? UpdateRequested;
    private readonly Button update = new() { Text = "업데이트 확인", AutoSize = true, FlatStyle = FlatStyle.Flat };

    internal void SetUpdateStatus(string? status)
    {
        update.Enabled = status is null;
        update.Text = status ?? "업데이트 확인";
    }

    internal void SetReleaseNotice(UpdateNotice? notice)
    {
        int height = notice is null ? 0 : (int)Math.Round(190 * DeviceDpi / 96f);
        int difference = height - (int)noticeRow.Height;
        noticeRow.Height = height;
        noticePanel.Visible = notice is not null;
        noticeTitle.Text = notice is null ? "" : "업데이트 " + notice.VersionText + " · 변경 내용";
        noticeContent.Text = notice is null ? "" : string.IsNullOrWhiteSpace(notice.Notes) ? "이 버전의 변경 내용이 등록되지 않았습니다." :
            System.Text.RegularExpressions.Regex.Replace(notice.Notes, @"(?m)^#{1,6}\s+", "").Replace("**", "");
        if (difference != 0)
        {
            MinimumSize = Size.Empty;
            ClientSize = new Size(ClientSize.Width, ClientSize.Height + difference);
            MinimumSize = Size;
            if (IsHandleCreated)
            {
                var work = Screen.FromControl(this).WorkingArea;
                Location = new Point(Math.Clamp(Left, work.Left, Math.Max(work.Left, work.Right - Width)),
                    Math.Clamp(Top, work.Top, Math.Max(work.Top, work.Bottom - Height)));
            }
        }
    }

    internal ConnectionForm(UsageSource codex, UsageSource claude, Action<UsageSource, bool> setEnabled)
        : this(new[] { codex, claude }, setEnabled) { }

    internal ConnectionForm(IEnumerable<UsageSource> sources, Action<UsageSource, bool> setEnabled)
    {
        this.sources = sources.ToArray();
        toggles = new CheckBox[this.sources.Length];
        statuses = new Label[this.sources.Length];
        connectButtons = new Button[this.sources.Length];
        refreshButtons = new Button[this.sources.Length];
        this.setEnabled = setEnabled;
        Text = "정보";
        Font = bodyFont;
        BackColor = Color.FromArgb(24, 24, 24);
        ForeColor = Color.FromArgb(232, 232, 232);
        ClientSize = new Size(690, 184 + this.sources.Length * 62);
        MinimumSize = ClientSize;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        AutoScaleMode = AutoScaleMode.Dpi;

        layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(18), ColumnCount = 4, RowCount = this.sources.Length + 4 };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 115));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 168));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 46));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 43));
        foreach (var _ in this.sources) layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 62));
        layout.RowStyles.Add(noticeRow);
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
        var intro = new Label { Text = "아이콘 클릭하면 회전 및 연결 새로고침", Dock = DockStyle.Fill, AutoSize = false };
        layout.Controls.Add(intro, 0, 0);
        layout.SetColumnSpan(intro, 4);
        for (int index = 0; index < this.sources.Length; index++)
        {
            int selected = index;
            var source = this.sources[index];
            toggles[index] = new CheckBox { Text = source.Name, AutoSize = true, Checked = source.Enabled, Anchor = AnchorStyles.Left };
            toggles[index].CheckedChanged += (_, _) =>
            {
                if (updating) return;
                this.setEnabled(source, toggles[selected].Checked);
                RefreshRows();
            };
            statuses[index] = new Label { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(0, 0, 6, 0) };
            connectButtons[index] = new ConnectButton { Text = "브라우저 로그인", Dock = DockStyle.Fill, Margin = new Padding(0, 12, 0, 12), FlatStyle = FlatStyle.Flat, BackColor = Color.FromArgb(45, 45, 45), UseVisualStyleBackColor = false };
            connectButtons[index].Click += async (_, _) => await ConnectAsync(source);
            refreshButtons[index] = new RefreshButton { AccessibleName = source.Name + " 새로고침", AccessibleDescription = "연결 새로고침", Dock = DockStyle.Fill, Margin = new Padding(6, 12, 0, 12), FlatStyle = FlatStyle.Flat, BackColor = Color.FromArgb(45, 45, 45), UseVisualStyleBackColor = false };
            refreshButtons[index].Click += async (_, _) => await source.RefreshAsync();
            connectButtons[index].FlatAppearance.BorderColor = refreshButtons[index].FlatAppearance.BorderColor = Color.FromArgb(80, 80, 80);
            layout.Controls.Add(toggles[index], 0, index + 1);
            layout.Controls.Add(statuses[index], 1, index + 1);
            layout.Controls.Add(connectButtons[index], 2, index + 1);
            layout.Controls.Add(refreshButtons[index], 3, index + 1);
            source.Changed += RefreshRows;
        }
        int noticeIndex = this.sources.Length + 1;
        noticePanel.Controls.Add(noticeContent);
        noticePanel.Controls.Add(noticeTitle);
        layout.Controls.Add(noticePanel, 0, noticeIndex);
        layout.SetColumnSpan(noticePanel, 4);
        int helpRow = noticeIndex + 1;
        layout.Controls.Add(outcome, 0, helpRow);
        layout.SetColumnSpan(outcome, 2);
        cancel.Dock = DockStyle.Fill;
        cancel.FlatStyle = FlatStyle.Flat;
        cancel.BackColor = Color.FromArgb(45, 45, 45);
        cancel.UseVisualStyleBackColor = false;
        cancel.Margin = new Padding(0, 4, 0, 10);
        layout.Controls.Add(cancel, 2, helpRow);
        layout.SetColumnSpan(cancel, 2);
        var footer = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight, WrapContents = false };
        reopen.LinkColor = Color.FromArgb(125, 209, 238);
        reopen.ActiveLinkColor = Color.White;
        reopen.LinkClicked += (_, _) => { if (loginUrl is not null) OpenUrl(loginUrl); };
        cancel.Click += (_, _) => loginCancellation?.Cancel();
        footer.Controls.Add(reopen);
        update.BackColor = Color.FromArgb(45, 45, 45);
        update.UseVisualStyleBackColor = false;
        update.FlatAppearance.BorderColor = cancel.FlatAppearance.BorderColor = Color.FromArgb(80, 80, 80);
        update.Click += (_, _) => UpdateRequested?.Invoke(this, EventArgs.Empty);
        footer.Controls.Add(update);
        layout.Controls.Add(footer, 0, helpRow + 1);
        layout.SetColumnSpan(footer, 4);
        Controls.Add(layout);
        RefreshRows();
    }

    private void RefreshRows()
    {
        if (IsDisposed || Disposing) return;
        updating = true;
        for (int index = 0; index < sources.Length; index++)
        {
            var source = sources[index];
            toggles[index].Checked = source.Enabled;
            connectButtons[index].Enabled = loginCancellation is null && source.Enabled && !source.Refreshing;
            refreshButtons[index].Enabled = loginCancellation is null && source.Enabled && !source.Refreshing;
            statuses[index].Text = (source.Gemini ? "실험 · " : "") + (source.Refreshing ? "로그인·사용량 확인 중…" : source.Status);
            if (source.Claude && !source.Refreshing && source.Data?.PlanLabel is null && source.Detail is not null)
                statuses[index].Text += " · " + source.Detail;
        }
        updating = false;
        if (connectingSource is { Enabled: false }) loginCancellation?.Cancel();
    }

    private async Task ConnectAsync(UsageSource source)
    {
        if (loginCancellation is not null || !source.Enabled) return;
        if (!OfficialLogin.IsAvailable(source.Kind))
        {
            outcome.Text = source.Name + " 로그인 도구가 없습니다. 공식 CLI 설치 후 다시 연결하세요.";
            return;
        }
        using var cancellation = new CancellationTokenSource();
        using var client = new OfficialLogin();
        loginCancellation = cancellation;
        login = client;
        connectingSource = source;
        loginUrl = null;
        source.SetConnecting(true);
        cancel.Visible = true;
        outcome.Text = "브라우저에서 로그인과 계정 연결을 완료해 주세요.";
        RefreshRows();
        try
        {
            bool success = await client.RunAsync(source.Kind, url =>
            {
                if (IsDisposed || Disposing || !IsHandleCreated) return;
                try { BeginInvoke(() => { if (!IsDisposed && ReferenceEquals(loginCancellation, cancellation)) { loginUrl = url; reopen.Visible = true; } }); }
                catch (InvalidOperationException) { }
            }, cancellation.Token);
            source.SetConnecting(false);
            if (success && source.Enabled)
            {
                source.Reconnect();
                await source.TickAsync(true);
            }
            if (!IsDisposed) outcome.Text = success ? "로그인 절차 완료 · " + source.Status : "로그인이 완료되지 않았습니다. 다시 시도할 수 있습니다.";
        }
        catch (OperationCanceledException) { if (!IsDisposed) outcome.Text = "로그인을 취소했습니다."; }
        catch (Exception error) when (error is InvalidOperationException or IOException or System.ComponentModel.Win32Exception)
        { if (!IsDisposed) outcome.Text = error is InvalidOperationException ? error.Message : "로그인 도구를 실행하지 못했습니다. 설치 상태를 확인해 주세요."; }
        finally
        {
            source.SetConnecting(false);
            loginCancellation = null;
            login = null;
            connectingSource = null;
            loginUrl = null;
            if (!IsDisposed)
            {
                cancel.Visible = false;
                reopen.Visible = false;
                RefreshRows();
            }
        }
    }

    private void OpenUrl(Uri url)
    {
        try { Process.Start(new ProcessStartInfo(url.AbsoluteUri) { UseShellExecute = true }); }
        catch (System.ComponentModel.Win32Exception) { outcome.Text = "기본 브라우저를 열지 못했습니다. Windows의 기본 브라우저 설정을 확인해 주세요."; }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        int dark = 1, background = ColorTranslator.ToWin32(BackColor), text = ColorTranslator.ToWin32(ForeColor);
        DwmSetWindowAttribute(Handle, 20, ref dark, sizeof(int));
        DwmSetWindowAttribute(Handle, 35, ref background, sizeof(int));
        DwmSetWindowAttribute(Handle, 36, ref text, sizeof(int));
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        loginCancellation?.Cancel();
        login?.Dispose();
        foreach (var source in sources) source.Changed -= RefreshRows;
        base.OnFormClosed(e);
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing) bodyFont.Dispose();
    }
}
