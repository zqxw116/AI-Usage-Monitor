using System.Reflection;
using System.Runtime.InteropServices;
using UsageMonitor;

internal static class InfoViewChecks
{
    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr window, int attribute, out int value, int size);
    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);

    internal static void Run(string directory, Action<bool, string> check)
    {
        using var codex = new UsageSource(false);
        using var claude = new UsageSource(true);
        using var monitor = new MonitorForm(codex, claude, false);
        using var dialog = new ConnectionForm(new[] { codex, claude }, monitor.SetServiceEnabled) { Opacity = 0 };
        dialog.Show();
        Application.DoEvents();
        var controls = Descendants(dialog).ToArray();
        check(dialog.Text == "정보", "관리 창 이름을 정보로 표시");
        check(controls.OfType<Label>().Any(label => label.Text == "아이콘 클릭하면 회전 및 연결 새로고침"), "정보 창 상단에 아이콘 동작 한 줄 안내");
        check(!controls.OfType<LinkLabel>().Any(link => link.Text.Contains("설치 안내")), "하단 서비스 설치 안내 제거");
        check(!controls.OfType<Label>().Any(label => label.Text.StartsWith("회전 화살표는")), "상시 표시하던 긴 기술 설명 제거");
        check(dialog.ClientSize.Height < 350 && dialog.BackColor.R == dialog.BackColor.G && dialog.BackColor.G == dialog.BackColor.B,
            "설명 영역을 줄인 중성 다크 테마 정보 창");
        if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000))
        {
            check(DwmGetWindowAttribute(dialog.Handle, 20, out int dark, sizeof(int)) == 0 && dark == 1,
                "Windows 제목 표시줄 다크 모드 활성화");
            // DWMWA_CAPTION_COLOR is a setter attribute; querying it returns E_INVALIDARG.
            int caption = ColorTranslator.ToWin32(dialog.BackColor);
            check(DwmSetWindowAttribute(dialog.Handle, 35, ref caption, sizeof(int)) == 0,
                "Windows 제목 표시줄에 본문과 같은 사용자 지정 다크 색상 적용 지원");
        }
        var tooltip = (ToolTip)typeof(MonitorForm).GetField("tooltip", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(monitor)!;
        typeof(MonitorForm).GetMethod("OnMouseMove", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(monitor, new object[] { new MouseEventArgs(MouseButtons.None, 0, 36, 17, 0) });
        check(tooltip.GetToolTip(monitor) == "아이콘 클릭: 연결 새로고침", "AI 영역 마우스 안내는 새로고침 한 줄만 표시");
        using var screenshot = new Bitmap(dialog.Width, dialog.Height);
        dialog.DrawToBitmap(screenshot, new Rectangle(Point.Empty, dialog.Size));
        screenshot.Save(Path.Combine(directory, "정보 다크 테마.png"));
        dialog.Close();
    }

    private static IEnumerable<Control> Descendants(Control control)
    {
        foreach (Control child in control.Controls)
        {
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }
}
