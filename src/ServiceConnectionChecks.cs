using System.Net;

namespace UsageMonitor;

internal static class ServiceConnectionChecks
{
    private sealed class Handler : HttpMessageHandler
    {
        public int Calls;
        public Func<CancellationToken, Task<HttpResponseMessage>> Reply = _ => Task.FromResult(Response());
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellation)
        { Calls++; return Reply(cancellation); }
    }

    private static HttpResponseMessage Response() => new(HttpStatusCode.OK)
    {
        Content = new StringContent("{\"five_hour\":{\"utilization\":29,\"resets_at\":null},\"seven_day\":null}")
    };

    internal static void Run(string directory, Action<bool, string> assert)
    {
        var fixture = Path.Combine(directory, "서비스 연결 합성 검증");
        Directory.CreateDirectory(fixture);
        string settingsFile = Path.Combine(fixture, "settings.json");
        var settings = new MonitorSettings { CodexEnabled = false, ClaudeEnabled = true };
        settings.Save(settingsFile);
        var restored = MonitorSettings.Load(settingsFile);
        assert(!restored.CodexEnabled && restored.ClaudeEnabled, "서비스별 활성화 선택을 파일에 저장하고 복원");
        File.WriteAllText(settingsFile, "invalid json");
        restored = MonitorSettings.Load(settingsFile);
        assert(restored.CodexEnabled && restored.ClaudeEnabled, "손상된 설정은 기본값으로 복구");
        File.WriteAllText(settingsFile, "{\"ClaudeEnabled\":false}");
        restored = MonitorSettings.Load(settingsFile);
        assert(restored.CodexEnabled && !restored.ClaudeEnabled, "일부 설정만 있어도 나머지 서비스 기본 활성화 유지");

        var handler = new Handler();
        using var http = new HttpClient(handler);
        using var source = new UsageSource(true, http, fixture, () => new ClaudeDesktopCredential { Bearer = "synthetic-desktop" });
        source.SetEnabled(false);
        source.TickAsync(true).GetAwaiter().GetResult();
        assert(handler.Calls == 0 && source.Data is null && !source.Failed && source.Status == "비활성", "비활성 서비스는 강제 갱신에도 인증·API 조회 중지");
        source.SetEnabled(true);
        source.TickAsync().GetAwaiter().GetResult();
        assert(handler.Calls == 1 && source.Data?.FiveHour?.Remaining == 71, "다시 활성화하면 기존 로그인으로 즉시 조회");
        source.SetConnecting(true);
        source.TickAsync(true).GetAwaiter().GetResult();
        assert(handler.Calls == 1 && source.Data is null && source.Connecting, "수동 로그인 중에는 기존 계정 수치와 자동 조회 중지");
        source.SetConnecting(false);
        source.TickAsync(true).GetAwaiter().GetResult();
        string connectedStatus = source.Status;
        source.SetConnecting(false);
        assert(handler.Calls == 2 && !source.Failed && source.Status == connectedStatus, "로그인 완료 후 조회 복구 및 중복 종료 처리로 상태 덮어쓰기 방지");

        var pending = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        bool cancelled = false;
        handler.Reply = token => { token.Register(() => cancelled = true); return pending.Task; };
        Task request = source.TickAsync(true);
        source.SetEnabled(false);
        source.SetEnabled(true);
        pending.SetResult(Response());
        request.GetAwaiter().GetResult();
        assert(cancelled && source.Data is null && !source.Failed && source.Status == "연결 중", "조회 도중 껐다 켜도 이전 응답이 새 활성 상태를 덮어쓰지 않음");
        handler.Reply = _ => Task.FromResult(Response());
        source.TickAsync().GetAwaiter().GetResult();
        assert(source.Data is not null && !source.Failed, "취소된 조회 이후 다음 자동 조회 정상 복구");
        using var codex = new UsageSource(false, http);
        codex.SetEnabled(false);
        int before = handler.Calls;
        codex.TickAsync(true).GetAwaiter().GetResult();
        assert(handler.Calls == before && source.Enabled && source.Data is not null, "Codex만 꺼도 Claude 활성 상태와 사용량 유지");

        var home = Path.Combine(fixture, "home");
        var roaming = Path.Combine(fixture, "roaming");
        var bin = Path.Combine(fixture, "tools with spaces & symbols");
        Directory.CreateDirectory(bin);
        string executable = Path.Combine(bin, "codex.exe");
        File.WriteAllBytes(executable, Array.Empty<byte>());
        var command = OfficialLogin.FindCommand(false, new[] { bin }, home, roaming)!;
        var start = OfficialLogin.CreateStartInfo(command, home);
        assert(start.FileName == executable && !start.UseShellExecute && start.CreateNoWindow && start.RedirectStandardError,
            "공백·특수문자 경로의 공식 실행 파일을 셸 없이 숨김 실행");
        assert(start.ArgumentList.SequenceEqual(new[] { "-c", "cli_auth_credentials_store=\"file\"", "login" }), "Codex는 파일 저장 설정과 로그인 명령만 실행");
        File.WriteAllBytes(Path.Combine(bin, "claude.exe"), Array.Empty<byte>());
        command = OfficialLogin.FindCommand(true, new[] { bin }, home, roaming)!;
        assert(command.Arguments.SequenceEqual(new[] { "auth", "login" }), "Claude는 로그인 전용 명령만 실행");
        assert(OfficialLogin.FindCommand(false, Array.Empty<string>(), home, roaming) is null, "미설치 상태에서 임의 명령 실행 없이 미발견 반환");
        assert(OfficialLogin.ParseLoginUrl("https://auth.openai.com/oauth/authorize?state=synthetic") is not null &&
            OfficialLogin.ParseLoginUrl("https://claude.com/cai/oauth/authorize?state=synthetic") is not null, "공식 브라우저 로그인 URL만 인식");
        assert(OfficialLogin.ParseLoginUrl("https://auth.openai.com.attacker.example/oauth/authorize") is null &&
            OfficialLogin.ParseLoginUrl("https://attacker@auth.openai.com/oauth/authorize") is null &&
            OfficialLogin.ParseLoginUrl("http://auth.openai.com/oauth/authorize") is null &&
            OfficialLogin.ParseLoginUrl("https://auth.openai.com/oauth/token?access_token=synthetic") is null,
            "위조 도메인·사용자정보·HTTP·토큰 응답 URL을 로그인 링크로 사용하지 않음");
        CheckLoginProcess(assert);
    }

    private static void CheckLoginProcess(Action<bool, string> assert)
    {
        string shell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");
        const string output = "[Console]::Out.WriteLine('https://auth.openai.com/oauth/authorize?state=synthetic');";
        var command = new OfficialLogin.LoginCommand(shell, new[] { "-NoProfile", "-NonInteractive", "-Command", output + "exit 0" });
        using (var login = new OfficialLogin())
        {
            int observed = 0;
            bool success = login.RunCommandAsync(command, _ => observed++, CancellationToken.None).GetAwaiter().GetResult();
            assert(success && observed == 1, "모의 로그인 프로세스 출력에서 공식 URL을 감지하고 정상 종료 처리");
        }
        command = new OfficialLogin.LoginCommand(shell, new[] { "-NoProfile", "-NonInteractive", "-Command", output + "Start-Sleep -Seconds 30" });
        foreach (bool dispose in new[] { false, true })
        {
            using var login = new OfficialLogin();
            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(12));
            var ready = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            Task<bool> running = login.RunCommandAsync(command, _ => ready.TrySetResult(true), cancellation.Token);
            bool started = ready.Task.Wait(8000);
            if (dispose) login.Dispose();
            else cancellation.Cancel();
            bool cancelled = false;
            try { running.GetAwaiter().GetResult(); }
            catch (OperationCanceledException) { cancelled = true; }
            assert(started && cancelled, dispose ? "연결 창 종료 시 자신이 시작한 로그인 프로세스 취소·종료" : "로그인 취소 시 자신이 시작한 프로세스 취소·종료");
        }
    }

    internal static void RunVisual(string directory, Action<bool, string> assert)
    {
        using var codex = new UsageSource(false);
        using var claude = new UsageSource(true);
        using var monitor = new MonitorForm(codex, claude, false);
        monitor.Opacity = 0;
        monitor.Show();
        Application.DoEvents();
        var original = monitor.ClientSize;
        var menu = monitor.ButtonBounds(8);
        IntPtr point = (IntPtr)(((int)(menu.Y + menu.Height / 2) << 16) | (int)(menu.X + menu.Width / 2));
        MonitorForm.SendMessage(monitor.Handle, 0x201, (IntPtr)1, point);
        assert(monitor.ToolsExpanded && monitor.ClientSize.Width == original.Width + 94 && monitor.ClientSize.Height == original.Height,
            "실제 메뉴 버튼 클릭으로 창 높이 유지하며 조작 목록 펼치기");
        menu = monitor.ButtonBounds(8);
        point = (IntPtr)(((int)(menu.Y + menu.Height / 2) << 16) | (int)(menu.X + menu.Width / 2));
        MonitorForm.SendMessage(monitor.Handle, 0x201, (IntPtr)1, point);
        assert(!monitor.ToolsExpanded && monitor.ClientSize == original, "같은 메뉴 버튼을 다시 누르면 목록 접기 및 원래 크기 복원");
        using var dialog = new ConnectionForm(codex, claude, monitor.SetServiceEnabled) { Opacity = 0 };
        dialog.Show();
        Application.DoEvents();
        var layout = dialog.Controls.OfType<TableLayoutPanel>().Single();
        var checks = layout.Controls.OfType<CheckBox>().ToArray();
        var loginButtons = layout.Controls.OfType<Button>().Where(button => button.Text == "브라우저 로그인").ToArray();
        assert(checks.Length == 2 && checks.All(check => check.Checked) && loginButtons.Length == 2 && loginButtons.All(button => button.Enabled),
            "연결 관리에 두 서비스 선택과 브라우저 로그인 버튼 표시");
        checks.Single(check => check.Text == "Claude").Checked = false;
        assert(codex.Enabled && !claude.Enabled && loginButtons.Count(button => button.Enabled) == 1,
            "화면에서 Claude만 비활성화하면 해당 로그인 버튼과 조회만 중지");
        assert(monitor.DesignSize == new Size(530, 50) && monitor.ClientSize == new Size(530, 50), "Claude를 끄면 해당 칸이 사라지고 Codex 한 칸으로 창 축소");
        checks.Single(check => check.Text == "Codex").Checked = false;
        assert(!codex.Enabled && !claude.Enabled && loginButtons.All(button => !button.Enabled), "두 서비스 모두 비활성화 허용");
        assert(monitor.DesignSize == new Size(290, 50) && MonitorForm.WithinDesktop(monitor.Bounds), "두 서비스가 꺼져도 작은 안내와 메뉴를 화면 안에 유지");
        using (var screenshot = new Bitmap(monitor.Width, monitor.Height))
        {
            monitor.DrawToBitmap(screenshot, new Rectangle(Point.Empty, monitor.Size));
            screenshot.Save(Path.Combine(directory, "두 서비스 비활성.png"));
        }
        checks.Single(check => check.Text == "Claude").Checked = true;
        assert(!codex.Enabled && claude.Enabled && loginButtons.Count(button => button.Enabled) == 1, "화면에서 Claude만 다시 활성화 가능");
        assert(monitor.DesignSize == new Size(530, 50) && monitor.ClientSize == new Size(530, 50), "Codex가 꺼져 있으면 Claude만 한 칸으로 표시");
        using (var screenshot = new Bitmap(monitor.Width, monitor.Height))
        {
            monitor.DrawToBitmap(screenshot, new Rectangle(Point.Empty, monitor.Size));
            screenshot.Save(Path.Combine(directory, "Claude만 표시.png"));
        }
        monitor.ToggleLayout();
        assert(monitor.DesignSize == new Size(530, 50), "세로 모드에서도 서비스가 하나면 한 줄 높이 유지");
        checks.Single(check => check.Text == "Codex").Checked = true;
        assert(monitor.DesignSize == new Size(530, 100), "세로 모드에서 두 번째 서비스를 켜면 두 줄로 확장");
        monitor.SetServiceEnabled(codex, false);
        assert(monitor.DesignSize == new Size(530, 50), "세로 모드에서 서비스 하나를 끄면 빈 줄 제거");
        monitor.ClientSize = new Size(424, 40);
        monitor.ToggleTools();
        assert(monitor.ClientSize == new Size(499, 40), "작게 조절한 단일 서비스 창에서도 배율 유지하며 메뉴 펼치기");
        monitor.ToggleTools();
        assert(monitor.ClientSize == new Size(424, 40), "메뉴를 접으면 기존 축소 배율과 높이 복원");
        using (var screenshot = new Bitmap(dialog.Width, dialog.Height))
        {
            dialog.DrawToBitmap(screenshot, new Rectangle(Point.Empty, dialog.Size));
            screenshot.Save(Path.Combine(directory, "서비스 연결 관리.png"));
        }
        dialog.Close();
    }
}
