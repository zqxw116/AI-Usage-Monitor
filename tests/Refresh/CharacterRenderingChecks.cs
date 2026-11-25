using UsageMonitor;
using System.Reflection;
using System.Runtime.InteropServices;

internal static class CharacterRenderingChecks
{
    [DllImport("user32.dll")] private static extern int GetGuiResources(IntPtr process, int flags);
    [DllImport("dwmapi.dll")] private static extern int DwmFlush();
    [DllImport("user32.dll")] private static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr window, out MonitorForm.NativeRect rectangle);
    private sealed class WindowChanges : NativeWindow, IDisposable
    {
        internal readonly List<int> Messages = new();
        internal WindowChanges(IntPtr handle) => AssignHandle(handle);
        protected override void WndProc(ref Message message)
        {
            if (message.Msg is 0x46 or 0x47 or 5)
            {
                Messages.Add(message.Msg);
            }
            base.WndProc(ref message);
        }
        public void Dispose() => ReleaseHandle();
    }

    internal static void Run(Action<bool, string> check)
    {
        using var owner = new Form { Opacity = 0, ShowInTaskbar = false };
        owner.Show();
        using var walker = new OrangeWalker();
        using var layer = new OutsideCharacterForm();
        var area = Screen.PrimaryScreen!.WorkingArea;
        var center = new PointF(area.Left + area.Width / 2f, area.Top + area.Height / 2f);
        layer.UpdatePose(owner, walker, center, 200, 0, 0, area);
        using var changes = new WindowChanges(layer.Handle);
        layer.UpdatePose(owner, walker, new PointF(center.X + 20, center.Y + 10), 300, 0, 0, area);
        Console.WriteLine("알파 창 프레임 메시지: " + string.Join(",", changes.Messages.Select(value => value.ToString("X"))));
        check(layer.Width == 302 && layer.Height == 302, "알파 프레임 갱신 후 관리 창 크기 일치");
        check(changes.Messages.Count == 0, "새 이미지를 적용하기 전에 창만 따로 이동·리사이즈하지 않음");
        using var process = System.Diagnostics.Process.GetCurrentProcess();
        int resources = GetGuiResources(process.Handle, 0);
        var surfaceField = typeof(OutsideCharacterForm).GetField("visitSurface", BindingFlags.Instance | BindingFlags.NonPublic)!;
        Bitmap? first = null;
        bool reused = true, offsetPixels = false, hitAligned = false;
        for (int frame = 0; frame <= 30; frame++)
        {
            var pose = CharacterVisit.PoseAt(frame * CharacterVisit.Duration / 30d, center, 200, 0, area, walker.Face);
            layer.UpdatePose(owner, walker, pose.Center, pose.Size, pose.Angle, 0, area, smooth: true, visiting: true);
            var surface = (Bitmap)surfaceField.GetValue(layer)!;
            first ??= surface;
            reused &= ReferenceEquals(first, surface) && surface.Size == area.Size;
            if (frame != 0) continue;
            var offset = new Point(layer.Left - area.Left, layer.Top - area.Top);
            using var expected = new Bitmap(layer.Width, layer.Height, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
            using (var graphics = Graphics.FromImage(expected))
                walker.DrawSprite(graphics, new PointF(pose.Center.X - layer.Left, pose.Center.Y - layer.Top), 0, 0, pose.Size, smooth: true);
            bool matches = offset.X > 0 && offset.Y > 0;
            for (int y = 0; y < expected.Height; y++)
            for (int x = 0; x < expected.Width; x++)
            {
                var pixel = expected.GetPixel(x, y);
                int difference = Math.Abs(surface.GetPixel(x + offset.X, y + offset.Y).A - pixel.A);
                // GDI+ 보간은 큰 버퍼의 절대 좌표에서 알파가 최대 2 차이납니다.
                matches &= difference <= 2;
                if (pixel.A < 200) continue;
                int screenX = layer.Left + x, screenY = layer.Top + y;
                hitAligned |= MonitorForm.SendMessage(layer.Handle, 0x84, IntPtr.Zero,
                    (IntPtr)((screenY << 16) | (screenX & 0xffff))) == (IntPtr)1;
            }
            offsetPixels = matches;
        }
        check(offsetPixels, "확대 버퍼의 비영점 소스 오프셋에 실제 그림 정렬");
        check(hitAligned, "확대 버퍼의 비영점 소스 오프셋에 클릭 좌표 정렬");
        check(reused, "확대·중앙 이동·축소 전체에서 같은 작업 영역 버퍼 재사용");
        layer.ReleaseVisitSurface();
        check(surfaceField.GetValue(layer) is null && GetGuiResources(process.Handle, 0) <= resources + 2,
            "확대와 축소 종료 후 방문 버퍼와 GDI 자원 해제");
        CheckMirror(walker, check);
        CheckShrinkTrail(owner, walker, layer, surfaceField, check);
    }

    private static void CheckMirror(OrangeWalker walker, Action<bool, string> check)
    {
        const int side = 240;
        var center = new PointF(side / 2f, side / 2f);
        using var normal = new Bitmap(side, side);
        using var mirrored = new Bitmap(side, side);
        using (var graphics = Graphics.FromImage(normal)) walker.DrawSprite(graphics, center, 0, 0, 200);
        using (var graphics = Graphics.FromImage(mirrored)) walker.DrawSprite(graphics, center, 0, 0, 200, mirrorX: true);
        bool pixelsMatch = true, hitMatches = true, asymmetric = false;
        for (int y = 0; y < side; y++)
        for (int x = 0; x < side; x++)
        {
            pixelsMatch &= normal.GetPixel(side - 1 - x, y) == mirrored.GetPixel(x, y);
            var point = new PointF(x + .5f, y + .5f);
            var opposite = new PointF(side - point.X, point.Y);
            bool hit = walker.HitTestSprite(point, center, 0, 0, 200, mirrorX: true);
            hitMatches &= hit == walker.HitTestSprite(opposite, center, 0, 0, 200);
            asymmetric |= hit != walker.HitTestSprite(point, center, 0, 0, 200);
        }
        check(pixelsMatch, "수평 반전 렌더링은 원본 좌우 픽셀의 정확한 역순");
        check(hitMatches && asymmetric, "좌우 반전한 실제 그림에 맞게 클릭 영역도 반전");
    }

    private static void CheckShrinkTrail(Form owner, OrangeWalker walker, OutsideCharacterForm layer,
        FieldInfo surfaceField, Action<bool, string> check)
    {
        var area = Screen.PrimaryScreen!.WorkingArea;
        var backdrop = new Rectangle(area.Left + 80, area.Top + 90, 400, 300);
        owner.BackColor = Color.FromArgb(77, 55, 91);
        owner.FormBorderStyle = FormBorderStyle.None;
        owner.Bounds = backdrop;
        owner.Opacity = 1;
        owner.TopMost = true;
        owner.Refresh();
        Application.DoEvents();
        bool clear = true;
        foreach (bool mirror in new[] { false, true })
        {
            layer.UpdatePose(owner, walker, new PointF(backdrop.Left + 240, backdrop.Top + 150), 260, 0, 0,
                backdrop, smooth: true, visiting: true, mirrorX: mirror);
            layer.UpdatePose(owner, walker, new PointF(backdrop.Left + 100, backdrop.Top + 110), 70, 0, 0,
                backdrop, smooth: true, visiting: true, mirrorX: mirror);
            var surface = (Bitmap)surfaceField.GetValue(layer)!;
            var current = new Rectangle(64, 74, 72, 72);
            for (int y = 0; y < backdrop.Height; y += 3)
            for (int x = 0; x < backdrop.Width; x += 3)
                if (!current.Contains(x, y)) clear &= surface.GetPixel(x, y).A == 0;
        }
        check(clear, "크게 확대 후 이동·축소·반전해도 이전 버퍼 픽셀은 완전히 투명");
        DwmFlush();
        bool desktopClear = true;
        var previousDpi = SetThreadDpiAwarenessContext((IntPtr)(-4));
        try
        {
            GetWindowRect(owner.Handle, out var physicalOwner);
            GetWindowRect(layer.Handle, out var physicalLayer);
            var actual = physicalOwner.Rectangle;
            var current = physicalLayer.Rectangle;
            current.Offset(-actual.Left, -actual.Top);
            current.Inflate(3, 3);
            using var capture = new Bitmap(actual.Width, actual.Height);
            using (var graphics = Graphics.FromImage(capture)) graphics.CopyFromScreen(actual.Location, Point.Empty, actual.Size);
            capture.Save(Path.Combine(AppContext.BaseDirectory, "shrink-trail-screen.png"));
            for (int y = 8; y < capture.Height - 8; y += 5)
            for (int x = 8; x < capture.Width - 8; x += 5)
            {
                if (current.Contains(x, y)) continue;
                var color = capture.GetPixel(x, y);
                desktopClear &= Math.Abs(color.R - owner.BackColor.R) <= 3 && Math.Abs(color.G - owner.BackColor.G) <= 3 &&
                    Math.Abs(color.B - owner.BackColor.B) <= 3;
            }
        }
        finally { SetThreadDpiAwarenessContext(previousDpi); }
        check(desktopClear, "실제 합성 화면에서도 축소 전 위치에 캐릭터 잔상이 남지 않음");
        layer.ReleaseVisitSurface();
    }
}
