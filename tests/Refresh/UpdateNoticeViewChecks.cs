using UsageMonitor;

internal static class UpdateNoticeViewChecks
{
    internal static void Run(string directory, Action<bool, string> check)
    {
        string noticeDirectory = Path.Combine(directory, "update-notice-view");
        const string notes = "글자 확대 품질 개선\n\n- 로그인 갱신 안정화\n- 무지개 팔레트 유지\n원문 링크: https://example.com/release-note";
        var version = AppUpdater.CurrentVersion;
        var release = new UpdateRelease(version,
            new Uri($"https://github.com/zqxw116/AI-Usage-Monitor/releases/download/v{version.ToString(3)}/AI-Usage-Monitor-win-x64.exe"),
            new string('a', 64), 1234, notes);
        check(UpdateNoticeStore.Save(release, noticeDirectory), "합성 현재 버전 업데이트 공지 저장");
        var notice = UpdateNoticeStore.LoadForCurrentVersion(version, noticeDirectory);
        check(notice is { IsRead: false }, "재시작 시 현재 버전의 읽지 않은 공지 복원");
        if (notice is null) return;

        using var codex = new UsageSource(false);
        using var claude = new UsageSource(true);
        MonitorSettings Settings() => new() { CodexEnabled = false, ClaudeEnabled = false, GeminiEnabled = false };
        using (var monitor = new MonitorForm(codex, claude, live: false, settings: Settings(), notice: notice, noticeDirectory: noticeDirectory) { Opacity = 0 })
        {
            monitor.Show();
            Application.DoEvents();
            check(!codex.Enabled && !claude.Enabled, "공지 UI 검사 중 모든 서비스 비활성화");
            check(monitor.HasUnreadUpdate && monitor.BlinkingNoticeButton == 8,
                "닫힌 메뉴에서는 읽지 않은 공지를 메뉴 버튼으로 알림");
            check(Changes(monitor, 8), "닫힌 메뉴 버튼의 두 위상 프레임이 실제로 다름");

            monitor.ToggleTools();
            check(monitor.HasUnreadUpdate && monitor.BlinkingNoticeButton == 7,
                "메뉴를 열면 정보 버튼으로 깜빡임 대상 전환");
            check(Changes(monitor, 7), "열린 정보 버튼의 두 위상 프레임이 실제로 다름");
            check(UpdateNoticeStore.LoadForCurrentVersion(version, noticeDirectory)?.IsRead == false,
                "메뉴만 열어서는 공지를 읽음 처리하지 않음");

            Click(monitor, 0);
            check(monitor.HasUnreadUpdate && UpdateNoticeStore.LoadForCurrentVersion(version, noticeDirectory)?.IsRead == false,
                "다른 도구 버튼 클릭으로 공지를 읽음 처리하지 않음");
            monitor.ToggleTools();
            check(monitor.BlinkingNoticeButton == 8 && monitor.HasUnreadUpdate,
                "메뉴를 다시 닫아도 미확인 알림 유지");

            monitor.ShowConnections();
            Application.DoEvents();
            var passiveInfo = monitor.OwnedForms.OfType<ConnectionForm>().Single();
            passiveInfo.Opacity = 0;
            check(monitor.HasUnreadUpdate && UpdateNoticeStore.LoadForCurrentVersion(version, noticeDirectory)?.IsRead == false,
                "정보 창을 일반 경로로 열어도 공지를 읽음 처리하지 않음");
            passiveInfo.Close();

            monitor.ToggleTools();
            Click(monitor, 7);
            Application.DoEvents();
            var info = monitor.OwnedForms.OfType<ConnectionForm>().Single();
            info.Opacity = 0;
            var controls = Descendants(info).ToArray();
            var content = controls.OfType<RichTextBox>().SingleOrDefault(text => text.Text.Replace("\r\n", "\n").Contains(notes));
            check(content is { ReadOnly: true, DetectUrls: false }, "정보 버튼 클릭 후 읽기 전용 공지 원문 표시 및 자동 링크 비활성화");
            check(controls.Any(control => control.Text.Contains(version.ToString(3))), "정보 창에 업데이트 버전 표시");
            check(controls.OfType<Label>().Any(label => label.Text == "아이콘 클릭하면 회전 및 연결 새로고침"),
                "공지 표시 중에도 상단 한 줄 안내 유지");
            check(!controls.OfType<LinkLabel>().Any(link => link.Text.Contains("설치 안내")), "공지에 설치 안내 링크를 다시 추가하지 않음");
            check(!monitor.HasUnreadUpdate && monitor.BlinkingNoticeButton == -1,
                "정보 버튼 직접 클릭으로 읽음 처리하고 깜빡임 종료");
            check(UpdateNoticeStore.LoadForCurrentVersion(version, noticeDirectory)?.IsRead == true,
                "정보 버튼 확인 상태를 공지 저장소에 영속화");
            check(!Changes(monitor, 7), "읽음 처리 후 정보 버튼은 시간에 따라 깜빡이지 않음");
            info.Close();
            monitor.Close();
        }
        var readNotice = UpdateNoticeStore.LoadForCurrentVersion(version, noticeDirectory);
        using var restarted = new MonitorForm(codex, claude, live: false, settings: Settings(), notice: readNotice, noticeDirectory: noticeDirectory) { Opacity = 0 };
        restarted.Show();
        Application.DoEvents();
        check(!restarted.HasUnreadUpdate && restarted.BlinkingNoticeButton == -1 && !Changes(restarted, 8),
            "창을 다시 생성해도 확인한 공지가 다시 깜빡이지 않음");
        restarted.Close();
    }

    private static void Click(MonitorForm monitor, int button)
    {
        var bounds = monitor.ButtonBounds(button);
        int x = (int)((bounds.X + bounds.Width / 2) * monitor.ClientSize.Width / monitor.DesignSize.Width);
        int y = (int)((bounds.Y + bounds.Height / 2) * monitor.ClientSize.Height / monitor.DesignSize.Height);
        var location = (IntPtr)((y << 16) | (x & 0xffff));
        MonitorForm.SendMessage(monitor.Handle, 0x201, (IntPtr)1, location);
        MonitorForm.SendMessage(monitor.Handle, 0x202, IntPtr.Zero, location);
    }

    private static bool Changes(MonitorForm monitor, int button)
    {
        using var first = Frame(monitor, 0);
        using var second = Frame(monitor, .6);
        var bounds = monitor.ButtonBounds(button);
        float sx = monitor.ClientSize.Width / (float)monitor.DesignSize.Width;
        float sy = monitor.ClientSize.Height / (float)monitor.DesignSize.Height;
        var region = Rectangle.Ceiling(new RectangleF(bounds.X * sx, bounds.Y * sy, bounds.Width * sx, bounds.Height * sy));
        for (int y = region.Top; y < region.Bottom; y++)
            for (int x = region.Left; x < region.Right; x++)
                if (first.GetPixel(x, y) != second.GetPixel(x, y)) return true;
        return false;
    }

    private static Bitmap Frame(MonitorForm monitor, double seconds)
    {
        monitor.PreviewOrange(.2, seconds);
        var image = new Bitmap(monitor.ClientSize.Width, monitor.ClientSize.Height);
        monitor.DrawToBitmap(image, new Rectangle(Point.Empty, monitor.ClientSize));
        return image;
    }

    private static IEnumerable<Control> Descendants(Control parent)
    {
        foreach (Control child in parent.Controls)
        {
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }
}
