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
        if (args.Length == 2 && args[0] == "--verify")
        {
            Verify(args[1], codex, claude);
            return;
        }
        string mutexName = args.Length == 2 && args[0] == "--watch-test" ? "Local\\HaneulAiUsageMonitorVerification" : "Local\\HaneulAiUsageMonitor";
        using var instance = new Mutex(true, mutexName, out bool first);
        if (!first) return;
        using var form = new MonitorForm(codex, claude);
        if (args.Length == 2 && args[0] == "--watch-test")
        {
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
        using (var car = new OrangeWalker(OrangeWalker.Characters[2]))
        {
            Assert(car.Height(new Size(594, 100)) == 33 && car.Height(new Size(1094, 50)) == 33, "빨간 자동차 차체 확대 후 얇은 창에서도 크기 증가 여유 유지");
            car.ChangeSize(-4, new Size(594, 100));
            Assert(car.Height(new Size(594, 100)) == 29, "자동차 크기 버튼 4픽셀 조절");
            car.ChangeSize(4, new Size(1094, 50));
            Assert(car.Height(new Size(1094, 50)) == 33, "자동차 축소 후 확대 복원");
            car.ChangeSize(4, new Size(1094, 50));
            Assert(car.Height(new Size(1094, 50)) == 37, "가로 모드에서도 자동차 확대 버튼이 실제 크기를 증가시킴");
            using var next = new OrangeWalker(OrangeWalker.Characters[4], car.PreferredSize);
            Assert(next.Height(new Size(594, 100)) == car.PreferredSize, "캐릭터 변경 시 자동차 확대 배율이 다른 캐릭터에 전이되지 않음");
        }
        for (int selection = 0; selection < OrangeWalker.Characters.Length; selection++)
        {
            using var character = new OrangeWalker(OrangeWalker.Characters[selection]);
            Assert(character.FrameCount == new[] { 32, 79, 92, 14, 8, 8 }[selection], OrangeWalker.Characters[selection].Name + " 캐릭터 프레임 로드");
            foreach (var canvas in new[] { new Size(1094, 50), new Size(594, 100) })
            foreach (int delta in new[] { -100, 14, 100 })
            {
                character.ChangeSize(delta, canvas);
                double loop = character.Perimeter(canvas);
                for (int i = 0; i < 500; i++)
                {
                    var pose = character.PoseAt(loop * i / 500, canvas);
                    if (pose.Bounds.Left < 1 || pose.Bounds.Top < 1 || pose.Bounds.Right > canvas.Width - 1 || pose.Bounds.Bottom > canvas.Height - 1)
                        throw new InvalidOperationException("캐릭터 회전·잘림 검증 실패");
                }
            }
        }
        tests.Add("여섯 캐릭터·두 모드·세 크기: 18000개 위치에서 회전 시 잘림 없음");
        foreach (int selection in new[] { 4, 5 })
        {
            using var character = new OrangeWalker(OrangeWalker.Characters[selection], 64);
            Assert(character.CycleMilliseconds == 800 && character.FrameAt(0) != character.FrameAt(0.1) && character.FrameAt(0.8) == 0,
                OrangeWalker.Characters[selection].Name + " 8프레임 걷기 순환 재생");
            var walkDirectory = Path.Combine(directory, "걷기 동작 " + selection);
            Directory.CreateDirectory(walkDirectory);
            for (int frame = 0; frame < 8; frame++)
            {
                using var image = new Bitmap(160, 100);
                using var graphics = Graphics.FromImage(image);
                graphics.Clear(Color.FromArgb(24, 28, 34));
                character.Draw(graphics, new Size(160, 100), frame * 0.1 + 0.001, 46);
                image.Save(Path.Combine(walkDirectory, frame.ToString("D3") + ".png"));
            }
            using var firstWalk = new Bitmap(Path.Combine(walkDirectory, "000.png"));
            using var secondWalk = new Bitmap(Path.Combine(walkDirectory, "003.png"));
            int difference = 0;
            for (int y = 0; y < 100; y++) for (int x = 0; x < 160; x++)
                if (firstWalk.GetPixel(x, y) != secondWalk.GetPixel(x, y)) difference++;
            Assert(difference > 0, OrangeWalker.Characters[selection].Name + " 동일 위치에서 몸·다리 동작 프레임이 실제로 변화");
        }
        using (var sprite = new OrangeWalker())
        {
            Assert(sprite.FrameCount == 32 && sprite.CycleMilliseconds == 1060, "오렌지 GIF 32프레임과 원본 1.06초 재생 주기 유지");
            Assert(sprite.FrameAt(0) != sprite.FrameAt(0.53) && sprite.FrameAt(0) == sprite.FrameAt(1.06), "걷기 프레임 변화와 GIF 반복");
            foreach (var canvas in new[] { new Size(1094, 50), new Size(594, 100) })
            {
                foreach (int delta in new[] { -100, 14, 100 })
                {
                    sprite.ChangeSize(delta, canvas);
                    double length = sprite.Perimeter(canvas);
                    for (int step = 0; step < 1000; step++)
                    {
                        var pose = sprite.PoseAt(length * step / 1000, canvas);
                        if (pose.Bounds.Left < 1 || pose.Bounds.Top < 1 || pose.Bounds.Right > canvas.Width - 1 || pose.Bounds.Bottom > canvas.Height - 1)
                            throw new InvalidOperationException("오렌지 회전·크기·화면 경계 검증 실패");
                    }
                    var start = sprite.PoseAt(0, canvas);
                    Assert(start == sprite.PoseAt(length, canvas), "오렌지 한 바퀴 뒤 처음 위치·방향으로 복귀");
                }
                float inset = sprite.Height(canvas) / 2 + 2;
                double horizontal = canvas.Width - inset * 2, tall = canvas.Height - inset * 2;
                var poses = new[] { sprite.PoseAt(horizontal / 2, canvas), sprite.PoseAt(horizontal + tall / 2, canvas),
                    sprite.PoseAt(horizontal * 1.5 + tall, canvas), sprite.PoseAt(horizontal * 2 + tall * 1.5, canvas) };
                Assert(poses.Select(pose => pose.Angle).SequenceEqual(new[] { 0f, -90f, -180f, -270f }), "아래→오른쪽→위→왼쪽 순서로 발 방향을 90도씩 회전");
            }
            tests.Add("최소·기본·최대 크기와 가로·세로 모드: 6000개 위치에서 오렌지 잘림 없음");
            using var firstFrame = new Bitmap(120, 60);
            using var laterFrame = new Bitmap(120, 60);
            using (var graphics = Graphics.FromImage(firstFrame)) sprite.Draw(graphics, new Size(120, 60), 0, 40);
            using (var graphics = Graphics.FromImage(laterFrame)) sprite.Draw(graphics, new Size(120, 60), 0.53, 40);
            bool changed = false;
            for (int y = 0; y < 60; y++) for (int x = 0; x < 120; x++)
                if (firstFrame.GetPixel(x, y) != laterFrame.GetPixel(x, y)) changed = true;
            Assert(changed, "실제 렌더링한 GIF 걷기 프레임의 픽셀이 변화");
            Assert(firstFrame.GetPixel(0, 0).A == 0, "GIF 주변 투명 배경 유지");
        }
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
        using var form = new MonitorForm(codex, claude, false);
        form.CreateControl();
        // 표시된 창에서만 최상단 네이티브 스타일을 검증하며, 투명하게 표시해 사용자 화면을 가리지 않습니다.
        Assert(!form.ShowInTaskbar, "기본 창이 작업표시줄에 표시되지 않음");
        form.Opacity = 0;
        form.Show();
        Application.DoEvents();
        var window = form.Handle;
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
            int x = (int)Math.Round((design.Width - 94 + 14 + column * 22) * form.ClientSize.Width / (double)design.Width);
            int y = (int)Math.Round((bottom ? 36 : 14) * form.ClientSize.Height / (double)design.Height);
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
                if (!MonitorForm.WithinDesktop(resized) || Math.Abs(resized.Height - resized.Width * 50.0 / 1094) > 1)
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
        ClickButton(0);
        Assert(form.TopMost && (GetWindowLong(window, -20) & 8) != 0, "핀 버튼 클릭으로 Windows 최상단 스타일 활성화");
        ClickButton(0);
        Assert(!form.TopMost && (GetWindowLong(window, -20) & 8) == 0, "핀 버튼 재클릭으로 Windows 최상단 스타일 해제");
        float orangeBefore = form.OrangeHeight;
        ClickButton(2);
        Assert(form.OrangeHeight == orangeBefore + 4, "오렌지 ＋ 버튼 클릭으로 캐릭터만 확대");
        ClickButton(2, true);
        Assert(form.OrangeHeight == orangeBefore, "오렌지 − 버튼 클릭으로 캐릭터만 축소");
        form.ClientSize = new Size(10000, 10000);
        Assert(form.ClientSize == form.Size && MonitorForm.WithinDesktop(form.Bounds), "화면보다 크게 요청해도 실제 창과 그리기 영역이 일치하여 버튼이 잘리지 않음");
        form.ClientSize = new Size(1094, 50);
        form.PreviewOrange(0.15, 0.2);
        Capture("미리보기.png");
        form.ClientSize = new Size(875, 40);
        ClickButton(1);
        Assert(form.DesignSize == new Size(594, 100) && form.ClientSize == new Size(475, 80), "전환 버튼 클릭으로 배율을 유지한 Codex·Claude 두 줄 배치");
        Capture("세로 축소.png");
        ClickButton(1);
        Assert(form.DesignSize == new Size(1094, 50) && form.ClientSize == new Size(875, 40), "전환 버튼 재클릭으로 배율을 유지한 가로 배치 복원");
        Capture("가로 축소.png");
        form.ClientSize = new Size(1094, 50);
        ClickButton(1);
        Capture("세로 미리보기.png");
        for (int selection = 0; selection < OrangeWalker.Characters.Length; selection++)
        {
            form.SelectCharacter(selection);
            Assert(form.SelectedCharacter == selection, "캐릭터 전환: " + OrangeWalker.Characters[selection].Name);
            form.PreviewOrange(0.15, 0.2);
            Capture("캐릭터 " + selection + ".png");
        }
        foreach (int selection in new[] { 4, 5 })
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
                Capture("오렌지 " + labels[i] + ".png");
            }
        }
        form.PreviewOrange(0.15, 0.2);
        form.ClientSize = new Size(743, 125);
        Capture("세로 확대.png");
        form.ToggleLayout();
        form.ClientSize = new Size(1094, 50);
        var report = new { tests, codex = new { codex.Status, codex.Failed, codex.Data }, claude = new { claude.Status, claude.Failed, claude.Data }, size = new { width = form.Width, height = form.Height }, design = form.DesignSize };
        File.WriteAllText(Path.Combine(directory, "검증결과.json"), JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
    }
}
