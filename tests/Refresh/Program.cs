using System.Net;
using UsageMonitor;

internal static class Program
{
    private static int failed;
    internal static void Check(bool passed, string name)
    {
        Console.WriteLine((passed ? "PASS " : "FAIL ") + name);
        if (!passed) failed++;
    }

    [STAThread]
    private static int Main(string[] args)
    {
        if (args.SequenceEqual(new[] { "--character-motion" }))
        {
            CharacterVisitChecks.Run(Check);
            CharacterRoamingChecks.Run(Check);
            return failed == 0 ? 0 : 1;
        }
        if (args.SequenceEqual(new[] { "--character-pointer" }))
        {
            CharacterPointerRegressionChecks.Run(Check, physicalInput: true);
            return failed == 0 ? 0 : 1;
        }
        if (args.SequenceEqual(new[] { "--character-interaction" }))
        {
            string path = Path.Combine(Path.GetTempPath(), "AIUsageInteraction-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path);
            try { CharacterInteractionChecks.Run(path, Check); CharacterDragChecks.Run(path, Check); CharacterPointerRegressionChecks.Run(Check); }
            finally { Directory.Delete(path, true); }
            return failed == 0 ? 0 : 1;
        }
        if (args.SequenceEqual(new[] { "--character-rendering" }))
        {
            CharacterRenderingChecks.Run(Check);
            return failed == 0 ? 0 : 1;
        }
        if (args.Length == 2 && args[0] == "--characters-preview")
        {
            Directory.CreateDirectory(args[1]);
            CharacterDisplayChecks.Run(args[1], Check, capture: true);
            CharacterVisitChecks.RenderPreview(args[1]);
            return failed == 0 ? 0 : 1;
        }
        if (args.SequenceEqual(new[] { "--characters" }))
        {
            string path = Path.Combine(Path.GetTempPath(), "AIUsageCharacters-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path);
            try { CharacterDisplayChecks.Run(path, Check); CharacterVisitChecks.Run(Check); CharacterRoamingChecks.Run(Check); CharacterRenderingChecks.Run(Check);
                CharacterInteractionChecks.Run(path, Check); CharacterDragChecks.Run(path, Check); CharacterPointerRegressionChecks.Run(Check); }
            finally { Directory.Delete(path, true); }
            return failed == 0 ? 0 : 1;
        }
        if (args.Length == 2 && args[0] == "--info-preview")
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Directory.CreateDirectory(args[1]);
            InfoViewChecks.Run(args[1], Check);
            return failed == 0 ? 0 : 1;
        }
        if (args.SequenceEqual(new[] { "--claude-desktop-watch" }))
            return WatchDesktopAsync().GetAwaiter().GetResult();
        if (args.SequenceEqual(new[] { "--claude-desktop-diagnostic" }))
        {
            using var source = new UsageSource(true, desktopOnly: true);
            source.TickAsync(true).GetAwaiter().GetResult();
            Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new
            {
                source.Status, source.Failed, HasUsage = source.Data is not null,
                FiveHourRemaining = source.Data?.FiveHour?.Remaining, WeeklyRemaining = source.Data?.Weekly?.Remaining,
                HasFiveHourReset = source.Data?.FiveHour?.Reset is not null,
                HasWeeklyReset = source.Data?.Weekly?.Reset is not null,
                source.Data?.Retrieved, source.Data?.PlanLabel
            }));
            return 0;
        }
        if (args.SequenceEqual(new[] { "--claude-login-diagnostic" }))
        {
            using var source = new UsageSource(true);
            source.RefreshAsync().GetAwaiter().GetResult();
            Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new
            {
                source.Status, source.Failed, HasUsage = source.Data is not null,
                FiveHourRemaining = source.Data?.FiveHour?.Remaining, WeeklyRemaining = source.Data?.Weekly?.Remaining
            }));
            return 0;
        }
        string directory = Path.Combine(Path.GetTempPath(), "AIUsageRefresh-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            CheckExpiredAsync(directory).GetAwaiter().GetResult();
            CheckRefreshAsync(directory).GetAwaiter().GetResult();
            CheckRateLimitAsync(directory).GetAwaiter().GetResult();
            CheckAuthCancellationAsync().GetAwaiter().GetResult();
            ServiceConnectionChecks.Run(directory, Check);
            CheckBundledCommand(directory);
            RenewalChecks.RunAsync(Check).GetAwaiter().GetResult();
            CheckRefreshControls(directory);
            PremiumPaletteChecks.Run(Check);
            InfoViewChecks.Run(directory, Check);
            UpdateNoticeViewChecks.Run(directory, Check);
            ResponsivenessChecks.Run(Check);
            CharacterDisplayChecks.Run(directory, Check);
            CharacterVisitChecks.Run(Check);
            CharacterRoamingChecks.Run(Check);
            CharacterRenderingChecks.Run(Check);
            CharacterInteractionChecks.Run(directory, Check);
            CharacterDragChecks.Run(directory, Check);
            CharacterPointerRegressionChecks.Run(Check);
        }
        finally { Directory.Delete(directory, true); }
        Console.WriteLine($"RESULT failed={failed}");
        return failed == 0 ? 0 : 1;
    }

    private static async Task CheckAuthCancellationAsync()
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var handler = new UsageHandler();
        using var client = new HttpClient(handler);
        using var source = new UsageSource(true, client, desktopReader: () =>
        {
            entered.Set();
            release.Wait(TimeSpan.FromSeconds(3));
            return new ClaudeDesktopCredential { Bearer = "synthetic-delayed" };
        }, desktopOnly: true);
        Task request = source.TickAsync(true);
        bool started = await Task.Run(() => entered.Wait(TimeSpan.FromSeconds(2)));
        source.SetEnabled(false);
        release.Set();
        await request;
        Check(started && handler.Calls == 0 && source.Data is null && !source.Failed,
            "느린 인증 읽기 중 비활성화하면 뒤늦은 인증으로 서버 요청하거나 결과를 표시하지 않음");
    }

    private static async Task<int> WatchDesktopAsync()
    {
        using var source = new UsageSource(true, desktopOnly: true);
        await source.TickAsync(true);
        DateTimeOffset? first = source.Data?.Retrieved;
        Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new { Phase = "initial", source.Status, source.Failed, Retrieved = first }));
        if (first is null || source.Failed) return 1;
        var watch = System.Diagnostics.Stopwatch.StartNew();
        while (watch.Elapsed < TimeSpan.FromSeconds(75))
        {
            await Task.Delay(1000);
            await source.TickAsync();
            if (source.Failed || source.Data is null || source.Data.Retrieved <= first) continue;
            Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new
            {
                Phase = "automatically-refreshed", source.Status, source.Failed, First = first, source.Data.Retrieved,
                HasFiveHourReset = source.Data.FiveHour?.Reset is not null, HasWeeklyReset = source.Data.Weekly?.Reset is not null
            }));
            return 0;
        }
        Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new { Phase = "timeout", source.Status, source.Failed }));
        return 1;
    }

    private static async Task CheckRefreshAsync(string directory)
    {
        string path = Path.Combine(directory, ".credentials.json");
        string Valid() => "{\"claudeAiOauth\":{\"accessToken\":\"synthetic-valid\",\"expiresAt\":" +
            DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeMilliseconds() + "}}";
        const string expired = """{"claudeAiOauth":{"accessToken":"synthetic-expired","refreshToken":"synthetic-refresh","expiresAt":1}}""";
        File.WriteAllText(path, expired);
        var handler = new UsageHandler();
        using var client = new HttpClient(handler);
        int renewals = 0;
        using (var source = new UsageSource(true, client, directory, () => null, claudeRenewal: (_, _) =>
        { renewals++; File.WriteAllText(path, Valid()); return Task.CompletedTask; }))
        {
            await source.TickAsync(true);
            Check(renewals == 1 && handler.Calls == 1 && source.Data?.FiveHour?.Remaining == 71 && !source.Failed,
                "공식 도우미 갱신 후 파일을 다시 읽고 새 인증으로 사용량 조회");
        }
        File.WriteAllText(path, expired);
        int before = handler.Calls;
        handler.Reply = _ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized));
        using (var source = new UsageSource(true, client, directory, () => null, claudeRenewal: (_, _) => Task.CompletedTask))
        {
            await source.TickAsync(true);
            Check(source.Failed && source.Status.Contains("만료") && handler.Calls == before + 1,
                "인증 도우미 성공 종료만으로는 만료된 로그인을 연결 성공으로 인정하지 않음");
        }
        File.WriteAllText(path, Valid());
        var pending = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        handler.Reply = _ => pending.Task;
        using (var source = new UsageSource(true, client, directory, () => null))
        {
            Task automatic = source.TickAsync(true);
            Task manual = source.RefreshAsync();
            Check(ReferenceEquals(manual, source.RefreshAsync()) && source.Refreshing, "중복 새로고침은 같은 작업을 공유");
            handler.Reply = _ => Task.FromResult(UsageHandler.Ok());
            pending.SetResult(UsageHandler.Ok());
            await automatic;
            await manual;
            Check(source.Data is not null && !source.Refreshing && source.Status.Contains("연결 확인됨"),
                "진행 중 자동 조회를 기다린 뒤 인증과 사용량을 다시 확인하고 회전 상태 종료");
            before = handler.Calls;
            await source.RefreshAsync();
            Check(handler.Calls == before + 1, "수동 새로고침은 자동 조회 대기 간격과 무관하게 즉시 조회");
            source.SetEnabled(false);
            before = handler.Calls;
            await source.RefreshAsync();
            Check(handler.Calls == before && !source.Refreshing && source.Status == "비활성", "비활성 서비스는 새로고침하지 않음");
        }
    }

    private static void CheckRefreshControls(string directory)
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        using var codex = new UsageSource(false);
        var handler = new UsageHandler();
        using var client = new HttpClient(handler);
        using var claude = new UsageSource(true, client, directory, () => null);
        using var monitor = new MonitorForm(codex, claude, false, new MonitorSettings { CodexEnabled = false });
        using var dialog = new ConnectionForm(new[] { codex, claude }, monitor.SetServiceEnabled);
        var refreshes = dialog.Controls.OfType<TableLayoutPanel>().Single().Controls.OfType<Button>()
            .Where(button => button.AccessibleName?.EndsWith("새로고침") == true).ToArray();
        Check(refreshes.Length == 2 && refreshes.All(button => button.Text.Length == 0), "연결 관리의 새로고침 버튼은 잘리는 글자 대신 아이콘 표시");
        monitor.Opacity = 0;
        monitor.Show();
        Application.DoEvents();
        int before = handler.Calls;
        MonitorForm.SendMessage(monitor.Handle, 0x201, (IntPtr)1, (IntPtr)((17 << 16) | 36));
        Pump(() => !claude.Refreshing);
        Check(handler.Calls == before + 1 && claude.Status.Contains("연결 확인됨"), "Claude 아이콘 클릭이 드래그 대신 실제 새로고침을 실행");
        using (var nameFirst = new Bitmap(monitor.Width, monitor.Height))
        using (var nameSecond = new Bitmap(monitor.Width, monitor.Height))
        {
            monitor.PreviewOrange(.5, 0);
            monitor.DrawToBitmap(nameFirst, monitor.ClientRectangle);
            monitor.PreviewOrange(.5, .3);
            monitor.DrawToBitmap(nameSecond, monitor.ClientRectangle);
            bool nameChanged = false;
            for (int y = 30; y < 46; y++) for (int x = 5; x < 67; x++)
                if (nameFirst.GetPixel(x, y) != nameSecond.GetPixel(x, y)) nameChanged = true;
            Check(nameChanged, "서비스 이름 글자가 순차적으로 움직이는 물결 프레임 표시");
        }
        var pending = new TaskCompletionSource<HttpResponseMessage>();
        handler.Reply = _ => pending.Task;
        Task refreshing = claude.RefreshAsync();
        Application.DoEvents();
        using var first = new Bitmap(monitor.Width, monitor.Height);
        using var second = new Bitmap(monitor.Width, monitor.Height);
        monitor.DrawToBitmap(first, monitor.ClientRectangle);
        Thread.Sleep(160);
        monitor.DrawToBitmap(second, monitor.ClientRectangle);
        bool changed = false;
        for (int y = 4; y < 29; y++) for (int x = 18; x < 54; x++)
            if (first.GetPixel(x, y) != second.GetPixel(x, y)) changed = true;
        Check(changed && claude.Refreshing, "실제 조회 대기 중 아이콘 회전 프레임이 변함");
        pending.SetResult(UsageHandler.Ok());
        Pump(() => refreshing.IsCompleted);
        dialog.Opacity = 0;
        dialog.Show();
        Application.DoEvents();
        before = handler.Calls;
        handler.Reply = _ => Task.FromResult(UsageHandler.Ok());
        refreshes.Single(button => button.AccessibleName == "Claude 새로고침").PerformClick();
        Pump(() => !claude.Refreshing);
        Check(handler.Calls == before + 1, "연결 관리 새로고침 버튼이 같은 서비스 조회를 실행");
        Check(!refreshes.Single(button => button.AccessibleName == "Codex 새로고침").Enabled, "비활성 서비스의 새로고침 버튼 비활성화");
        dialog.Close();
        before = handler.Calls;
        monitor.ShowConnections();
        var opened = Application.OpenForms.OfType<ConnectionForm>().Single();
        opened.Opacity = 0;
        Pump(() => !claude.Refreshing);
        Check(handler.Calls == before + 1, "서비스 연결 관리 열기도 활성 서비스의 같은 새로고침을 실행");
        opened.Close();
        monitor.Close();
    }

    private static void Pump(Func<bool> finished)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        while (!finished() && watch.Elapsed < TimeSpan.FromSeconds(5))
        { Application.DoEvents(); Thread.Sleep(1); }
        if (!finished()) throw new TimeoutException("합성 UI 작업이 완료되지 않았습니다.");
    }

    private static async Task CheckExpiredAsync(string directory)
    {
        string path = Path.Combine(directory, ".credentials.json");
        const string expired = """{"claudeAiOauth":{"accessToken":"synthetic-expired","expiresAt":1}}""";
        File.WriteAllText(path, expired);
        var handler = new UsageHandler();
        handler.Reply = _ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized));
        using var client = new HttpClient(handler);
        using (var restarted = new UsageSource(true, client, directory, () => null))
        {
            await restarted.TickAsync(true);
            Check(restarted.Failed && restarted.Data is null && restarted.Status.Contains("만료"),
                "재시작 후 만료 토큰을 연결 성공으로 표시하지 않고 만료 원인을 안내");
            Check(handler.Calls == 1, "로컬 만료 시각만으로 단정하지 않고 실제 서버 인증을 확인");
            Check(File.ReadAllText(path) == expired, "모니터가 인증 파일을 삭제하거나 덮어쓰지 않음");
        }
        handler.Reply = _ => Task.FromResult(UsageHandler.Ok());
        using var accepted = new UsageSource(true, client, directory, () => null);
        await accepted.RefreshAsync();
        Check(!accepted.Failed && accepted.Data is not null, "로컬 만료 시각이 과거여도 서버가 허용하면 실제 사용량 표시");
    }

    private static async Task CheckRateLimitAsync(string directory)
    {
        var handler = new UsageHandler();
        handler.Reply = _ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromMinutes(2));
            return Task.FromResult(response);
        };
        using var client = new HttpClient(handler);
        using var source = new UsageSource(true, client, directory, () => null);
        await source.RefreshAsync();
        await source.RefreshAsync();
        Check(handler.Calls == 1 && source.Failed && source.Status.Contains("조회 제한"),
            "조회 제한이면 수동 새로고침도 Retry-After 이전에 서버 재호출하지 않음");
    }

    private static void CheckBundledCommand(string directory)
    {
        foreach (string version in new[] { "2.1.314", "2.1.321", "2.1.322-beta" })
        {
            string build = Path.Combine(directory, "Claude-Data", "claude_fixture", "claude-code", version, "synthetic-build");
            Directory.CreateDirectory(build);
            File.WriteAllBytes(Path.Combine(build, "claude.exe"), Array.Empty<byte>());
        }
        var command = OfficialLogin.FindDesktopClaudeCommand(directory);
        Check(command is not null && command.FileName.Contains("2.1.321") && command.Arguments.SequenceEqual(new[] { "auth", "login" }),
            "별도 구형 npm 대신 Desktop에 포함된 지원 안정 버전 Code 탐색");
    }

    internal sealed class UsageHandler : HttpMessageHandler
    {
        internal int Calls;
        internal Func<CancellationToken, Task<HttpResponseMessage>> Reply = _ => Task.FromResult(Ok());
        internal static HttpResponseMessage Ok() => new(HttpStatusCode.OK)
        { Content = new StringContent("""{"five_hour":{"utilization":29},"seven_day":null}""") };
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellation)
        { Calls++; return Reply(cancellation); }
    }
}
