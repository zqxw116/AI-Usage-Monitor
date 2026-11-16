using System.Diagnostics;
using System.Reflection;
using UsageMonitor;

internal static class ResponsivenessChecks
{
    internal static void Run(Action<bool, string> check)
    {
        int phase = 0, readerThread = 0;
        int uiThread = Environment.CurrentManagedThreadId;
        bool notificationsOnUI = true;
        using var client = new HttpClient(new DelayedHandler(() => Volatile.Write(ref phase, 2)));
        using var claude = new UsageSource(true, client, desktopReader: () =>
        {
            Volatile.Write(ref phase, 1);
            readerThread = Environment.CurrentManagedThreadId;
            Thread.Sleep(650); // Stand in for a slow encrypted profile / SQLite read.
            return new ClaudeDesktopCredential { Bearer = "synthetic-desktop" };
        }, desktopOnly: true);
        claude.Changed += () => notificationsOnUI &= Environment.CurrentManagedThreadId == uiThread;
        using var codex = new UsageSource(false);
        using var monitor = new MonitorForm(codex, claude, false, new MonitorSettings { CodexEnabled = false });
        monitor.Opacity = 0;
        var animation = Field<System.Windows.Forms.Timer>(monitor, "orangeTimer");
        var clock = Field<Stopwatch>(monitor, "orangeClock");
        using var heartbeat = new System.Windows.Forms.Timer { Interval = 15 };
        var elapsed = new Stopwatch();
        long previous = 0, longestGap = 0;
        int authTicks = 0, networkTicks = 0;
        var authFrames = new HashSet<int>();
        var networkFrames = new HashSet<int>();
        heartbeat.Tick += (_, _) =>
        {
            long now = elapsed.ElapsedMilliseconds;
            longestGap = Math.Max(longestGap, now - previous);
            previous = now;
            if (Volatile.Read(ref phase) == 1) { authTicks++; authFrames.Add(monitor.OrangeFrame); }
            if (Volatile.Read(ref phase) == 2) { networkTicks++; networkFrames.Add(monitor.OrangeFrame); }
            if (!claude.Refreshing || elapsed.Elapsed > TimeSpan.FromSeconds(5))
            { heartbeat.Stop(); monitor.Close(); }
        };
        monitor.Shown += (_, _) =>
        {
            clock.Start();
            animation.Start();
            elapsed.Start();
            heartbeat.Start();
            check(SynchronizationContext.Current is WindowsFormsSynchronizationContext, "실제 WinForms UI 메시지 루프에서 검사 실행");
            // Exercise the real icon click handler rather than a different background entry point.
            MonitorForm.SendMessage(monitor.Handle, 0x201, (IntPtr)1, (IntPtr)((17 << 16) | 36));
        };
        Application.Run(monitor);
        Console.WriteLine($"UI auth ticks={authTicks}, network ticks={networkTicks}, longest gap={longestGap}ms");
        check(!claude.Refreshing && !claude.Failed && claude.Data is not null, "지연된 인증·서버 응답 이후 새로고침 정상 완료");
        check(readerThread != uiThread, "로컬 인증은 UI 스레드와 분리해서 읽음");
        check(notificationsOnUI, "조회 결과와 상태 알림은 UI 스레드에서 반영");
        check(authTicks >= 6 && authFrames.Count >= 2, "아이콘 새로고침의 느린 로컬 인증 읽기 중에도 실제 캐릭터 타이머 계속 실행");
        check(networkTicks >= 6 && networkFrames.Count >= 2, "서버 응답 대기 중에도 실제 캐릭터 타이머 계속 실행");
        check(longestGap < 350, "인증과 응답을 기다리는 동안 UI 메시지 처리가 멈추지 않음");
        monitor.Close();
    }

    private static T Field<T>(MonitorForm monitor, string name) =>
        (T)typeof(MonitorForm).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(monitor)!;

    private sealed class DelayedHandler(Action started) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellation)
        {
            started();
            await Task.Delay(650, cancellation);
            return Program.UsageHandler.Ok();
        }
    }
}
