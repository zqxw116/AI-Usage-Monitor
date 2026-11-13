using System.Text.Json;
using System.Runtime.InteropServices;

namespace UsageMonitor;

internal static class Program
{
    [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr window, int index);
    [STAThread]
    private static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        using var codex = new UsageSource(false);
        using var claude = new UsageSource(true);
        using var gemini = new UsageSource(ServiceKind.Gemini);
        if (args.Length == 2 && args[0] == "--verify")
        {
            Verify(args[1], codex, claude);
            return;
        }
        string mutexName = args.Length == 2 && args[0] == "--watch-test" ? "Local\\HaneulAiUsageMonitorVerification" : "Local\\HaneulAiUsageMonitor";
        using var instance = new Mutex(true, mutexName, out bool first);
        if (!first) return;
        using var form = new MonitorForm(codex, claude, settings: args.Length == 2 && args[0] == "--watch-test" ? new MonitorSettings() : null, gemini: gemini);
        if (args.Length == 2 && args[0] == "--watch-test")
        {
            form.Opacity = 0;
            Directory.CreateDirectory(args[1]);
            Snapshot? initialCodex = null, initialClaude = null;
            var started = DateTimeOffset.UtcNow;
            DateTimeOffset? baseline = null;
            var orangeAngles = new HashSet<int>();
            var orangeFrames = new HashSet<int>();
            var checkTimer = new System.Windows.Forms.Timer { Interval = 100 };
            checkTimer.Tick += (_, _) =>
            {
                orangeAngles.Add(form.OrangeAngle);
                orangeFrames.Add(form.OrangeFrame);
                if (baseline is null && codex.Data is not null && claude.Data is not null)
                {
                    initialCodex = codex.Data;
                    initialClaude = claude.Data;
                    baseline = DateTimeOffset.UtcNow;
                }
                if (baseline is not null && DateTimeOffset.UtcNow - baseline < TimeSpan.FromSeconds(65)) return;
                if (baseline is null && DateTimeOffset.UtcNow - started < TimeSpan.FromSeconds(90)) return;
                checkTimer.Stop();
                bool passed = initialCodex is not null && initialClaude is not null && !codex.Failed && !claude.Failed &&
                    codex.Data!.Retrieved > initialCodex.Retrieved && claude.Data!.Retrieved > initialClaude.Retrieved && orangeAngles.Count == 4 && orangeFrames.Count > 1;
                var result = new { passed, initialCodex, initialClaude, finalCodex = codex.Data, finalClaude = claude.Data, codexStatus = codex.Status, claudeStatus = claude.Status, orangeAngles, orangeFrameCount = orangeFrames.Count };
                File.WriteAllText(Path.Combine(args[1], "자동갱신실행검증.json"), JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
                using var bitmap = new Bitmap(form.Width, form.Height);
                form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, form.Size));
                bitmap.Save(Path.Combine(args[1], "실행화면.png"));
                form.Close();
            };
            form.Shown += (_, _) => checkTimer.Start();
            form.FormClosed += (_, _) => checkTimer.Dispose();
        }
        Application.Run(form);
    }

    private static void Verify(string directory, UsageSource codex, UsageSource claude)
    {
        Directory.CreateDirectory(directory);
        var tests = new List<string>();
        void Assert(bool condition, string name)
        {
            if (!condition) throw new InvalidOperationException("실패: " + name);
            tests.Add(name);
        }

        ServiceConnectionChecks.Run(directory, Assert);
        using (var json = JsonDocument.Parse("{\"five_hour\":{\"utilization\":91,\"resets_at\":\"2026-10-12T05:00:00Z\"},\"seven_day\":null}"))
        {
            var result = UsageParser.Parse(json.RootElement, true, DateTimeOffset.UtcNow);
            Assert(result.FiveHour?.Remaining == 9, "Claude 사용률을 남은 비율로 변환");
            Assert(result.Weekly is null, "미제공 한도를 100%로 오표시하지 않음");
        }
        using (var json = JsonDocument.Parse("{\"rate_limit\":{\"primary_window\":{\"used_percent\":31,\"reset_at\":1792300435,\"limit_window_seconds\":604800},\"secondary_window\":{\"used_percent\":1,\"reset_at\":1791790491,\"limit_window_seconds\":18000}}}"))
        {
            var result = UsageParser.Parse(json.RootElement, false, DateTimeOffset.UtcNow);
            Assert(result.FiveHour?.Remaining == 99 && result.Weekly?.Remaining == 69, "Codex 기간 길이로 5시간·주간 구분");
        }
        using (var json = JsonDocument.Parse("{\"five_hour\":{\"utilization\":101,\"resets_at\":null},\"seven_day\":{\"utilization\":0,\"resets_at\":null}}"))
        {
            var result = UsageParser.Parse(json.RootElement, true, DateTimeOffset.UtcNow);
            Assert(result.FiveHour is null, "범위 밖 사용률 거부");
            Assert(result.Weekly?.Remaining == 100 && result.Weekly.Reset is null, "0%와 시간 미제공 처리");
        }
        Assert(new Quota(75, DateTimeOffset.UtcNow.AddSeconds(-1)).Expired, "초기화 시각이 지난 데이터 표시 보류");
        Assert(MonitorForm.Countdown(DateTimeOffset.UtcNow.AddSeconds(-1)) == "초기화 여부 확인 중", "초기화 후 임의로 100%를 표시하지 않음");
        foreach (bool isClaude in new[] { false, true })
        {
            var bandColors = new[] { 90d, 70, 50, 30, 10 }.Select(value => MonitorForm.RemainingColor(value, isClaude)).ToArray();
            Assert(bandColors.Distinct().Count() == 5, (isClaude ? "Claude" : "Codex") + " 남은 비율 다섯 구간의 색상이 각각 구분됨");
            foreach (var boundary in new[] { (100d, 0), (80d, 0), (79.9, 1), (60d, 1), (59.9, 2), (40d, 2), (39.9, 3), (20.1, 3), (20d, 4), (0d, 4) })
                Assert(MonitorForm.RemainingColor(boundary.Item1, isClaude) == bandColors[boundary.Item2], (isClaude ? "Claude" : "Codex") + " 잔량 색상 경계 " + boundary.Item1 + "%");
        }
        Assert(MonitorForm.RemainingColor(100, false) == Color.FromArgb(81, 221, 190) && MonitorForm.RemainingColor(100, true) == Color.FromArgb(255, 180, 116), "Codex 청록색·Claude 주황색 시작");
        Assert(MonitorForm.RemainingColor(20, false) == Color.FromArgb(255, 112, 124) && MonitorForm.RemainingColor(0, true) == Color.FromArgb(255, 112, 124), "20% 이하 두 서비스 공통 빨간색 경고");
        var layouts = new[] { new[] { new Rectangle(-1920, 0, 1920, 1040), new Rectangle(0, 0, 1920, 1040), new Rectangle(1920, 100, 1280, 920) },
            new[] { new Rectangle(0, 0, 1920, 1080), new Rectangle(2200, 100, 1280, 900) } };
        var desktopRandom = new Random(79);
        foreach (var areas in layouts) for (int i = 0; i < 1000; i++)
        {
            var fitted = DesktopBounds.Fit(new Rectangle(desktopRandom.Next(-5000, 6000), desktopRandom.Next(-1000, 2500), desktopRandom.Next(200, 3000), desktopRandom.Next(20, 1200)), areas, new Size(1094, 50), i % 9);
            if (!DesktopBounds.Area(fitted, areas).Contains(fitted)) throw new InvalidOperationException("다중 모니터 경계 제한 검증 실패");
        }
        var crossing = DesktopBounds.Fit(new Rectangle(1700, 300, 1094, 50), layouts[0], new Size(1094, 50));
        Assert(crossing.X == 1700 && crossing.Right > 1920, "모니터 경계를 가로질러 좌우 이동 허용");
        tests.Add("서로 다른 높이·음수 좌표·화면 사이 빈틈 포함 2000개 다중 모니터 경계 사례 통과");
        PetChecks.Run(Path.Combine(directory, "펫 검증"), Assert);
        string? originalCodexHome = Environment.GetEnvironmentVariable("CODEX_HOME");
        string? originalClaudeHome = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
        var fixtureHome = Path.Combine(directory, "자동갱신검증");
        Directory.CreateDirectory(Path.Combine(fixtureHome, "codex", "sessions", "nested"));
        Directory.CreateDirectory(Path.Combine(fixtureHome, "claude", "projects", "nested"));
        try
        {
            Environment.SetEnvironmentVariable("CODEX_HOME", Path.Combine(fixtureHome, "codex"));
            Environment.SetEnvironmentVariable("CLAUDE_CONFIG_DIR", Path.Combine(fixtureHome, "claude"));
            using var fixtureCodex = new UsageSource(false);
            using var fixtureClaude = new UsageSource(true);
            File.WriteAllText(Path.Combine(fixtureHome, "codex", "sessions", "nested", "response.jsonl"), "{}\n");
            File.WriteAllText(Path.Combine(fixtureHome, "claude", "projects", "nested", "response.jsonl"), "{}\n");
            Assert(SpinWait.SpinUntil(() => fixtureCodex.HasActivity && fixtureClaude.HasActivity, 5000), "두 서비스의 하위 세션 파일 변경을 자동 감지");
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEX_HOME", originalCodexHome);
            Environment.SetEnvironmentVariable("CLAUDE_CONFIG_DIR", originalClaudeHome);
        }
        // No synchronization context here; wait for read-only live verification, then render on this STA thread.
        Task.WhenAll(codex.TickAsync(true), claude.TickAsync(true)).GetAwaiter().GetResult();
        ServiceConnectionChecks.RunVisual(directory, Assert);
        using (var unusedCodex = new UsageSource(false))
        using (var single = new MonitorForm(unusedCodex, claude, false, new MonitorSettings { CodexEnabled = false }))
        using (var screenshot = new Bitmap(single.Width, single.Height))
        {
            single.DrawToBitmap(screenshot, new Rectangle(Point.Empty, single.Size));
            screenshot.Save(Path.Combine(directory, "Claude만 실제 사용량.png"));
        }
        using (var palette = new MonitorForm(codex, claude, false))
        using (var image = new Bitmap(220, 50))
        using (var graphics = Graphics.FromImage(image))
        {
            foreach (bool isClaude in new[] { false, true })
            foreach (bool outdated in new[] { false, true })
            {
                graphics.Clear(Color.Black);
                palette.DrawQuota(graphics, new Quota(29, DateTimeOffset.UtcNow.AddHours(1)), 0, 0, "5시간", isClaude, outdated);
                Assert(image.GetPixel(10, 34) == MonitorForm.RemainingColor(71, isClaude),
                    (isClaude ? "Claude" : "Codex") + (outdated ? " 일시 오류·지연 중에도" : " 정상 조회 시") + " 71% 잔량 구간 색상으로 실제 렌더링");
                if (outdated) image.Save(Path.Combine(directory, isClaude ? "Claude 71% 조회 지연 색상.png" : "Codex 71% 조회 지연 색상.png"));
            }
            graphics.Clear(Color.Black);
            palette.DrawQuota(graphics, new Quota(29, DateTimeOffset.UtcNow.AddSeconds(-1)), 0, 0, "5시간", true, true);
            Assert(image.GetPixel(10, 34) == Color.FromArgb(12, 16, 22), "초기화 시각이 지난 값은 색상 바를 채우지 않고 확인 대기");
        }
        using var form = new MonitorForm(codex, claude, false);
        form.CreateControl();
        // 표시된 창에서만 최상단 네이티브 스타일을 검증하며, 투명하게 표시해 사용자 화면을 가리지 않습니다.
        Assert(!form.ShowInTaskbar, "기본 창이 작업표시줄에 표시되지 않음");
        form.Opacity = 0;
        form.Show();
        Application.DoEvents();
        var window = form.Handle;
        Assert(!form.ToolsExpanded && form.DesignSize == new Size(1030, 50), "기본 화면은 버튼 목록을 접은 가로 한 줄");
        form.ToggleTools();
        Assert((GetWindowLong(window, -20) & 0x40000) == 0, "실제 창의 작업표시줄 표시 스타일 비활성화");
        void Capture(string name)
        {
            using var image = new Bitmap(form.ClientSize.Width, form.ClientSize.Height);
            form.DrawToBitmap(image, new Rectangle(Point.Empty, form.ClientSize));
            image.Save(Path.Combine(directory, name));
        }
        void ClickButton(int column, bool bottom = false)
        {
            var design = form.DesignSize;
            var button = form.ButtonBounds((bottom ? 3 : 0) + column);
            int x = (int)Math.Round((button.X + button.Width / 2) * form.ClientSize.Width / design.Width);
            int y = (int)Math.Round((button.Y + button.Height / 2) * form.ClientSize.Height / design.Height);
            MonitorForm.SendMessage(window, 0x201, (IntPtr)1, (IntPtr)((y << 16) | x));
            MonitorForm.SendMessage(window, 0x202, IntPtr.Zero, (IntPtr)((y << 16) | x));
        }
        var random = new Random(42);
        foreach (var design in new[] { new Size(1094, 50), new Size(594, 100) })
        foreach (var area in new[] { new Rectangle(0, 0, 1920, 1040), new Rectangle(-1920, -200, 1920, 1040), new Rectangle(0, 0, 320, 240) })
        for (int i = 0; i < 200; i++)
        {
            var fitted = MonitorForm.FitBounds(new Rectangle(random.Next(-3000, 3000), random.Next(-2000, 2000), random.Next(1, 4000), random.Next(1, 2000)), area, design, i % 9);
            if (!area.Contains(fitted) || Math.Abs(fitted.Height - fitted.Width * design.Height / (double)design.Width) > 1)
                throw new InvalidOperationException("크기·화면 경계 검증 실패");
        }
        tests.Add("가로·세로 및 음수 모니터 좌표: 1200개 크기·화면 경계 사례 통과");
        var areaNow = Screen.FromHandle(window).WorkingArea;
        form.Location = new Point(areaNow.Right + 200, areaNow.Bottom + 200);
        Assert(MonitorForm.WithinDesktop(form.Bounds), "실제 창의 화면 밖 위치 요청 제한");
        var pointer = Marshal.AllocHGlobal(Marshal.SizeOf<MonitorForm.NativeRect>());
        try
        {
            for (int edge = 1; edge <= 8; edge++)
            {
                Marshal.StructureToPtr(new MonitorForm.NativeRect(new Rectangle(areaNow.Left - 100, areaNow.Top - 100, 900, 400)), pointer, false);
                MonitorForm.SendMessage(window, 0x214, (IntPtr)edge, pointer);
                var resized = Marshal.PtrToStructure<MonitorForm.NativeRect>(pointer).Rectangle;
                if (!MonitorForm.WithinDesktop(resized) || Math.Abs(resized.Height - resized.Width * 50.0 / 1124) > 1)
                    throw new InvalidOperationException("테두리 크기 조절 검증 실패");
            }
            tests.Add("Windows의 8방향 테두리 크기 조절 메시지: 비율·경계 유지");
            Marshal.StructureToPtr(new MonitorForm.NativeRect(new Rectangle(areaNow.Left - 200, areaNow.Top - 200, 1094, 50)), pointer, false);
            MonitorForm.SendMessage(window, 0x216, IntPtr.Zero, pointer);
            var moved = Marshal.PtrToStructure<MonitorForm.NativeRect>(pointer).Rectangle;
            Assert(MonitorForm.WithinDesktop(moved), "Windows 드래그 이동 메시지의 화면 경계 제한");
        }
        finally { Marshal.FreeHGlobal(pointer); }
        var corner = form.PointToScreen(new Point(form.ClientSize.Width - 1, form.ClientSize.Height - 1));
        var hit = MonitorForm.SendMessage(window, 0x84, IntPtr.Zero, (IntPtr)((corner.Y << 16) | (corner.X & 0xffff))).ToInt32();
        Assert(hit == 17, "실제 창 모서리에서 크기 조절 영역 인식");
        Assert(form.TopMost && (GetWindowLong(window, -20) & 8) != 0, "시작할 때 Windows 최상단 스타일 활성화");
        ClickButton(0);
        Assert(!form.TopMost && (GetWindowLong(window, -20) & 8) == 0, "핀 버튼 클릭으로 Windows 최상단 스타일 해제");
        ClickButton(0);
        Assert(form.TopMost && (GetWindowLong(window, -20) & 8) != 0, "핀 버튼 재클릭으로 최상단 복원");
        float orangeBefore = form.OrangeHeight;
        ClickButton(2);
        Assert(form.OrangeHeight == orangeBefore + 4, "캐릭터 ＋ 버튼 클릭으로 캐릭터만 확대");
        ClickButton(2, true);
        Assert(form.OrangeHeight == orangeBefore, "캐릭터 − 버튼 클릭으로 캐릭터만 축소");
        var originalSize = form.ClientSize;
        int buttonX = (int)((form.ButtonBounds(2).X + 8) * form.ClientSize.Width / (double)form.DesignSize.Width);
        int buttonY = (int)(9 * form.ClientSize.Height / (double)form.DesignSize.Height);
        using var buttonBefore = new Bitmap(form.ClientSize.Width, form.ClientSize.Height);
        form.DrawToBitmap(buttonBefore, new Rectangle(Point.Empty, form.ClientSize));
        for (int count = 0; count < 120; count++) ClickButton(2);
        Assert(form.OrangeHeight == orangeBefore + 480 && form.ClientSize == originalSize, "＋ 120회 클릭으로 창 크기 유지하며 계속 확대");
        form.PreviewOrange(0.5, 0.15);
        using var buttonAfter = new Bitmap(form.ClientSize.Width, form.ClientSize.Height);
        form.DrawToBitmap(buttonAfter, new Rectangle(Point.Empty, form.ClientSize));
        Assert(buttonBefore.GetPixel(buttonX, buttonY) == buttonAfter.GetPixel(buttonX, buttonY), "큰 캐릭터가 조절 버튼 위를 지나도 버튼을 가리지 않음");
        Capture("상한 없는 캐릭터 확대.png");
        for (int count = 0; count < 120; count++) ClickButton(2, true);
        Assert(form.OrangeHeight == orangeBefore, "크게 확대한 뒤 − 버튼으로 원래 크기 복원");
        form.ClientSize = new Size(10000, 10000);
        Assert(form.ClientSize == form.Size && MonitorForm.WithinDesktop(form.Bounds), "화면보다 크게 요청해도 실제 창과 그리기 영역이 일치하여 버튼이 잘리지 않음");
        form.ClientSize = new Size(1124, 50);
        form.PreviewOrange(0.15, 0.2);
        Capture("미리보기.png");
        form.ClientSize = new Size(899, 40);
        ClickButton(1);
        Assert(form.DesignSize == new Size(624, 100) && form.ClientSize == new Size(499, 80), "전환 버튼 클릭으로 배율을 유지한 Codex·Claude 두 줄 배치");
        Capture("세로 축소.png");
        ClickButton(1);
        Assert(form.DesignSize == new Size(1124, 50) && form.ClientSize == new Size(899, 40), "전환 버튼 재클릭으로 배율을 유지한 가로 배치 복원");
        Capture("가로 축소.png");
        form.ClientSize = new Size(1124, 50);
        ClickButton(1);
        Capture("세로 미리보기.png");
        for (int selection = 0; selection < OrangeWalker.Characters.Length; selection++)
        {
            form.SelectCharacter(selection);
            Assert(form.SelectedCharacter == selection, "캐릭터 전환: " + OrangeWalker.Characters[selection].Name);
            form.PreviewOrange(0.15, 0.2);
            Capture("캐릭터 " + selection + ".png");
        }
        foreach (int selection in Enumerable.Range(0, OrangeWalker.Characters.Length))
        {
            form.SelectCharacter(selection);
            form.ChangeOrangeSize(14);
            var frameDirectory = Path.Combine(directory, "PNG 이동 " + selection);
            Directory.CreateDirectory(frameDirectory);
            for (int frame = 0; frame < 96; frame++)
            {
                form.PreviewOrange(frame / 96.0, frame * 0.08);
                using var image = new Bitmap(form.ClientSize.Width, form.ClientSize.Height);
                form.DrawToBitmap(image, new Rectangle(Point.Empty, form.ClientSize));
                image.Save(Path.Combine(frameDirectory, frame.ToString("D3") + ".png"));
            }
            form.ChangeOrangeSize(-14);
        }
        form.SelectCharacter(0);
        form.CreateTray();
        Assert(form.TrayVisible, "트레이 아이콘 등록");
        Assert(form.TrayUsesApplicationIcon && form.Icon?.Width == 32, "사용자 이미지 아이콘을 창과 트레이에 적용");
        form.HideToTray();
        Assert(!form.Visible, "트레이로 창 숨기기");
        form.RestoreFromTray();
        Assert(form.Visible, "트레이에서 창 복원");
        Assert(!form.ShowInTaskbar && (GetWindowLong(form.Handle, -20) & 0x40000) == 0, "트레이에서 복원해도 작업표시줄에는 표시되지 않음");
        using (var previewSprite = new OrangeWalker())
        {
            var canvas = form.DesignSize;
            float inset = previewSprite.Height(canvas) / 2 + 2;
            double across = canvas.Width - inset * 2, up = canvas.Height - inset * 2;
            double loop = previewSprite.Perimeter(canvas);
            var positions = new[] { across / 2, across + up / 2, across * 1.5 + up, across * 2 + up * 1.5 };
            var labels = new[] { "아래", "오른쪽", "위", "왼쪽" };
            for (int i = 0; i < positions.Length; i++)
            {
                form.PreviewOrange(positions[i] / loop, i * 0.2);
                Capture("오리 " + labels[i] + ".png");
            }
        }
        form.PreviewOrange(0.15, 0.2);
        form.ClientSize = new Size(743, 125);
        Capture("세로 확대.png");
        form.ToggleLayout();
        form.ToggleTools();
        form.ClientSize = form.DesignSize;
        Capture("메뉴 접힘.png");
        var report = new { tests, codex = new { codex.Status, codex.Failed, codex.Data }, claude = new { claude.Status, claude.Failed, claude.Data }, size = new { width = form.Width, height = form.Height }, design = form.DesignSize };
        File.WriteAllText(Path.Combine(directory, "검증결과.json"), JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
    }
}
