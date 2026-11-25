using System.Reflection;
using System.Runtime.InteropServices;
using UsageMonitor;

internal static class CharacterPointerRegressionChecks
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    [DllImport("user32.dll")] private static extern void mouse_event(uint flags, uint x, uint y, uint data, UIntPtr extra);
    [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(Point point);
    private sealed class Trace(IntPtr handle) : NativeWindow, IDisposable
    {
        internal readonly List<int> Messages = new();
        internal void Attach() => AssignHandle(handle);
        protected override void WndProc(ref Message message)
        {
            if (message.Msg is 0x201 or 0x200 or 0x202 or 0x215) Messages.Add(message.Msg);
            base.WndProc(ref message);
        }
        public void Dispose() => ReleaseHandle();
    }
    internal static void Run(Action<bool, string> check, bool physicalInput = false)
    {
        using var codex = new UsageSource(false);
        using var claude = new UsageSource(true);
        var settings = new MonitorSettings { VisualEffects = false };
        using var form = new MonitorForm(codex, claude, false, settings);
        form.Show();
        var area = Screen.PrimaryScreen!.WorkingArea;
        form.Location = new Point(area.Left + 70, area.Top + 160);
        form.ChangeOrangeSize(80);
        Application.DoEvents();
        T Field<T>(string name) => (T)typeof(MonitorForm).GetField(name, Private)!.GetValue(form)!;
        var hitInside = typeof(MonitorForm).GetMethod("HitInsideCharacter", Private)!.CreateDelegate<Func<PointF, bool>>(form);
        IntPtr Pack(Point point) => (IntPtr)((point.Y << 16) | (point.X & 0xffff));
        void Send(Control target, int message, Point screen) => MonitorForm.SendMessage(target.Handle, message,
            message == 0x202 ? IntPtr.Zero : (IntPtr)1, Pack(target.PointToClient(screen)));
        Point InsidePixel(bool grip = false, bool interior = false)
        {
            float scale = form.ClientSize.Width / (float)form.DesignSize.Width;
            for (int y = 0; y < form.ClientSize.Height; y++)
            for (int x = 0; x < form.ClientSize.Width; x++)
            {
                if (interior && (x < 5 || y < 5 || x >= form.ClientSize.Width - 5 || y >= form.ClientSize.Height - 5)) continue;
                if (grip && x >= 4 && x < form.ClientSize.Width - 4 && y >= 4 && y < form.ClientSize.Height - 4) continue;
                var logical = new PointF(x / scale, y / scale);
                if (Enumerable.Range(form.ToolsExpanded ? 0 : 8, form.ToolsExpanded ? 9 : 1).Any(button => form.ButtonBounds(button).Contains(logical))) continue;
                if (hitInside(logical) && (!interior || new[] { new PointF(logical.X - 3, logical.Y), new PointF(logical.X + 3, logical.Y),
                    new PointF(logical.X, logical.Y - 3), new PointF(logical.X, logical.Y + 3) }.All(hitInside)))
                    return form.PointToScreen(new Point(x, y));
            }
            throw new InvalidOperationException("내부 실제 몸 픽셀을 찾지 못함");
        }
        Point OutsidePixel(OutsideCharacterForm overlay)
        {
            for (int y = overlay.Top; y < overlay.Bottom; y++)
            for (int x = overlay.Left; x < overlay.Right; x++)
                if (MonitorForm.SendMessage(overlay.Handle, 0x84, IntPtr.Zero, Pack(new Point(x, y))) == (IntPtr)1) return new Point(x, y);
            throw new InvalidOperationException("외부 실제 몸 픽셀을 찾지 못함");
        }
        form.PreviewOrange(.08, 0);
        var gripPoint = InsidePixel(grip: true);
        check(MonitorForm.SendMessage(form.Handle, 0x84, IntPtr.Zero, Pack(gripPoint)) == (IntPtr)1,
            "크기 조절 경계 위에 보이는 실제 몸 픽셀은 리사이즈 대신 HTCLIENT");
        using (var trace = new Trace(form.Handle))
        {
            trace.Attach();
            var down = InsidePixel();
            Send(form, 0x201, down);
            Send(form, 0x200, new Point(down.X + 8, down.Y));
            check(Field<bool>("draggingCharacter") && form.Capture && Field<Point?>("characterPress") is not null,
                "내부 스냅 프리뷰를 먼저 표시해도 드래그와 캡처 유지");
            var far = new Point(form.Left + form.Width / 2, form.Bottom + 130);
            Send(form, 0x200, far);
            check(Field<bool>("draggingCharacter") && form.Capture && Field<Point?>("characterPress") is not null,
                "실제 WM_MOUSEMOVE로 창 경계를 넘어도 내부 드래그와 캡처 유지");
            Send(form, 0x202, far);
            check(settings.CharacterDisplay == CharacterDisplay.FollowMouse && !form.Capture && Field<Point?>("characterPress") is null,
                "실제 WM_LBUTTONUP 이후 먼 드롭 적용과 캡처 해제 순서 유지");
            Console.WriteLine("실제 내부 포인터 메시지: " + string.Join(",", trace.Messages.Select(message => message.ToString("X"))));
        }
        foreach (var mode in new[] { CharacterDisplay.Inside, CharacterDisplay.Outside, CharacterDisplay.FreeRoam, CharacterDisplay.FollowMouse })
        {
            form.SetCharacterDisplay(mode);
            form.PreviewOrange(.08, 10);
            Control target = mode == CharacterDisplay.Inside ? form : form.OwnedForms.OfType<OutsideCharacterForm>().Single();
            var point = target == form ? InsidePixel() : OutsidePixel((OutsideCharacterForm)target);
            Send(target, 0x201, point);
            Send(target, 0x202, point);
            check(form.CharacterVisitActive, mode + " 실제 몸 짧은 클릭은 줍기 전에 확대 실행");
        }
        form.SetCharacterDisplay(CharacterDisplay.Inside);
        form.PreviewOrange(.08, 0);
        var savedCursor = Cursor.Position;
        try
        {
            foreach (var size in physicalInput ? new[] { 24, 104, 200 } : Array.Empty<int>())
            {
                form.SetCharacterDisplay(CharacterDisplay.Inside);
                form.ChangeOrangeSize(size - (int)Field<OrangeWalker>("orange").PreferredSize);
                form.PreviewOrange(.08, 0);
                form.Activate();
                form.BringToFront();
                Pump();
                Cursor.Position = InsidePixel(interior: true);
                Pump();
                check(WindowFromPoint(Cursor.Position) == form.Handle, size + "px 물리 포인터가 모니터 창 몸 픽셀에 도달");
                mouse_event(2, 0, 0, 0, UIntPtr.Zero);
                Pump();
                var down = Cursor.Position;
                Cursor.Position = new Point(down.X + 8, down.Y);
                Pump();
                check(Field<bool>("draggingCharacter") && form.Capture && Field<Point?>("characterPress") is not null,
                    size + "px 물리 입력으로 내부 첫 이동과 스냅 프리뷰 표시 후에도 드래그 캡처 유지");
                Cursor.Position = new Point(form.Left + form.Width / 2, form.Bottom + 180);
                Pump();
                mouse_event(4, 0, 0, 0, UIntPtr.Zero);
                Pump();
                check(settings.CharacterDisplay == CharacterDisplay.FollowMouse && !form.Capture && Field<Point?>("characterPress") is null,
                    size + "px 물리 입력으로 내부에서 프리뷰를 거쳐 바탕까지 떼어낸 뒤 추적 모드 전환");
            }
        }
        finally
        {
            mouse_event(4, 0, 0, 0, UIntPtr.Zero);
            Cursor.Position = savedCursor;
        }
        form.ChangeOrangeSize(104 - (int)Field<OrangeWalker>("orange").PreferredSize);
        form.SetCharacterDisplay(CharacterDisplay.Inside);
        form.PreviewOrange(.08, 0);
        var pressed = InsidePixel();
        Send(form, 0x201, pressed);
        var inWindow = new Point(form.Left + form.Width / 2, form.Bottom - 1);
        Send(form, 0x200, inWindow);
        Send(form, 0x202, inWindow);
        check(settings.CharacterDisplay == CharacterDisplay.Inside,
            "큰 몸의 발이 창 밖이어도 mouseup 커서가 창 안이면 항상 내부 궤도에 붙음");
        using (var large = new OrangeWalker(size: 200))
        {
            var box = new Rectangle(100, 100, 1030, 50);
            check(CharacterAttachment.Find(large, form.DesignSize, box, 1, new PointF(615, 125)) is { Mode: CharacterDisplay.Inside },
                "몸 크기와 테두리 거리와 무관하게 커서가 창 안이면 내부 nearest track 선택");
            check(CharacterAttachment.Find(large, form.DesignSize, box, 1, new PointF(80, 80)) is { Mode: CharacterDisplay.Outside } &&
                CharacterAttachment.Find(large, form.DesignSize, box, 1, new PointF(75, 75)) is null,
                "외부 스냅 band는 커서에서 창 사각형까지 대각 직선거리로 판정");
        }
        form.SetCharacterDisplay(CharacterDisplay.Inside);
        form.PreviewOrange(.08, 0);
        var hover = InsidePixel();
        Send(form, 0x200, hover);
        check(Field<bool>("characterHovered") && Field<double?>("hoverReactionStarted") == 0,
            "실제 몸 WM_MOUSEMOVE 진입 즉시 반응 시작하고 누르기는 필요 없음");
        form.PreviewOrange(.08, .05);
        MonitorForm.SendMessage(form.Handle, 0x2A3, IntPtr.Zero, IntPtr.Zero);
        form.PreviewOrange(.08, .06);
        Send(form, 0x200, InsidePixel());
        check(Field<double?>("hoverReactionStarted") == 0, "빠른 몸 재진입은 hover 느낌표를 반복 재시작하지 않음");
        MonitorForm.SendMessage(form.Handle, 0x2A3, IntPtr.Zero, IntPtr.Zero);
        form.PreviewOrange(.08, .4);
        Send(form, 0x200, InsidePixel());
        check(Field<double?>("hoverReactionStarted") == .4, "충분히 몸 밖에 머문 뒤 재진입하면 다시 반응");
        form.SetCharacterDisplay(CharacterDisplay.Inside);
        form.PreviewOrange(.08, 0);
        Send(form, 0x200, InsidePixel());
        var distanceBefore = Field<double>("orangeDistance");
        var clock = Field<System.Diagnostics.Stopwatch>("orangeClock");
        clock.Start(); Thread.Sleep(40);
        typeof(System.Windows.Forms.Timer).GetMethod("OnTick", Private)!.Invoke(Field<System.Windows.Forms.Timer>("orangeTimer"), new object[] { EventArgs.Empty });
        clock.Stop();
        var currentEffect = (CharacterEffect)typeof(MonitorForm).GetProperty("CurrentCharacterEffect", Private)!.GetValue(form)!;
        check(Field<double>("orangeDistance") != distanceBefore && Field<Point?>("characterPress") is null && currentEffect == CharacterEffect.Startled,
            "hover 반응 동안 실제 애니메이션 Tick에서도 위치 이동을 멈추지 않음");
        form.Close();
    }

    private static void Pump()
    {
        var until = Environment.TickCount64 + 80;
        while (Environment.TickCount64 < until) { Application.DoEvents(); Thread.Sleep(5); }
    }
}
