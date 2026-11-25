using System.Reflection;
using System.Runtime.InteropServices;
using UsageMonitor;

internal static class CharacterDisplayChecks
{
    [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr window, int index);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern int GetGuiResources(IntPtr process, int flags);
    [DllImport("dwmapi.dll")] private static extern int DwmFlush();

    internal static void Run(string directory, Action<bool, string> check, bool capture = false)
    {
        using var walker = new OrangeWalker(size: 24);
        var canvas = new Size(530, 50);
        double loop = walker.Perimeter(canvas, outside: true);
        float inset = walker.PoseAt(0, canvas, outside: true).Center.X;
        double horizontal = canvas.Width - inset * 2, vertical = canvas.Height - inset * 2;
        var poses = new[] { walker.PoseAt(horizontal / 2, canvas, true), walker.PoseAt(horizontal + vertical / 2, canvas, true),
            walker.PoseAt(horizontal * 1.5 + vertical, canvas, true), walker.PoseAt(horizontal * 2 + vertical * 1.5, canvas, true) };
        check(poses.Select(p => p.Angle).SequenceEqual(new[] { 0f, 90f, 180f, 270f }), "외곽 네 변에서 발을 박스 쪽으로 회전");
        check(poses[0].Bounds.Bottom <= 0 && poses[1].Bounds.Left >= canvas.Width &&
            poses[2].Bounds.Top >= canvas.Height && poses[3].Bounds.Right <= 0, "외부 캐릭터가 내부 정보를 덮지 않음");
        check(walker.PoseAt(0, canvas, true) == walker.PoseAt(loop, canvas, true) &&
            walker.PoseAt(-loop / 4, canvas, true) == walker.PoseAt(loop * .75, canvas, true), "외곽 무한 반복과 음수 거리 정규화");

        string path = Path.Combine(directory, "character-settings.json");
        var settings = new MonitorSettings { VisualEffects = false };
        check(settings.CharacterDisplay == CharacterDisplay.Inside, "기존 설정은 내부만 표시가 기본값");
        using var codex = new UsageSource(false);
        using var claude = new UsageSource(true);
        using var form = new MonitorForm(codex, claude, false, settings);
        using var backdrop = capture ? new Form { FormBorderStyle = FormBorderStyle.None, ShowInTaskbar = false,
            StartPosition = FormStartPosition.Manual, BackColor = Color.FromArgb(78, 88, 100) } : null;
        form.Show();
        Application.DoEvents();
        Size original = form.Size;
        Bitmap Capture()
        {
            var image = new Bitmap(form.Width, form.Height);
            form.DrawToBitmap(image, form.ClientRectangle);
            return image;
        }
        form.PreviewOrange(.08, 0);
        form.SetCharacterDisplay(CharacterDisplay.None);
        using var empty = Capture();
        foreach (var mode in Enum.GetValues<CharacterDisplay>())
        {
            form.SetCharacterDisplay(mode);
            var effectiveMode = mode == CharacterDisplay.Both ? CharacterDisplay.Inside : mode;
            Application.DoEvents();
            using var image = Capture();
            bool hasInside = false;
            for (int y = 0; y < image.Height && !hasInside; y++)
            for (int x = 0; x < image.Width && !hasInside; x++)
                hasInside = image.GetPixel(x, y) != empty.GetPixel(x, y);
            var overlay = form.OwnedForms.OfType<OutsideCharacterForm>().SingleOrDefault();
            check(hasInside == effectiveMode.HasFlag(CharacterDisplay.Inside), mode + " 내부 렌더링 선택 반영");
            check((overlay?.Visible == true) == (effectiveMode is CharacterDisplay.FreeRoam or CharacterDisplay.FollowMouse || effectiveMode.HasFlag(CharacterDisplay.Outside)), mode + " 외부 렌더링 선택 반영");
            settings.Save(path);
            check(MonitorSettings.Load(path).CharacterDisplay == effectiveMode, mode + " 재실행용 설정 저장");
            check(form.Size == original, mode + " 본체 창 크기 유지");
        }
        form.SetCharacterDisplay(CharacterDisplay.Outside);
        var outside = form.OwnedForms.OfType<OutsideCharacterForm>().Single();
        if (backdrop is not null)
        {
            var area = form.Bounds;
            area.Inflate(45, 45);
            backdrop.Bounds = area;
            backdrop.Show();
            form.BringToFront();
            using var strip = new Bitmap(area.Width, area.Height * 4);
            using var output = Graphics.FromImage(strip);
            double wide = form.DesignSize.Width + form.OrangeHeight;
            double tall = form.DesignSize.Height + form.OrangeHeight;
            double previewLoop = 2 * (wide + tall);
            foreach (int edge in Enumerable.Range(0, 4))
            {
                double fraction = (edge switch { 0 => wide / 2, 1 => wide + tall / 2,
                    2 => wide * 1.5 + tall, _ => wide * 2 + tall * 1.5 }) / previewLoop;
                form.PreviewOrange(fraction, edge * .15);
                Application.DoEvents();
                DwmFlush();
                using var screenshot = new Bitmap(area.Width, area.Height);
                using (var screen = Graphics.FromImage(screenshot))
                    screen.CopyFromScreen(area.Location, Point.Empty, area.Size);
                output.DrawImageUnscaled(screenshot, 0, edge * area.Height);
            }
            strip.Save(Path.Combine(directory, "캐릭터 외부 네 방향.png"));
            backdrop.Hide();
            form.PreviewOrange(.08, 0);
        }
        int style = GetWindowLong(outside.Handle, -20);
        check((style & 0x08080080) == 0x08080080 && !outside.ShowInTaskbar, "외부는 비활성·투명 도구 창이며 작업표시줄에 숨김");
        Point? opaque = null, transparent = null;
        for (int y = outside.Top; y < outside.Bottom; y++)
        for (int x = outside.Left; x < outside.Right; x++)
        {
            var hit = MonitorForm.SendMessage(outside.Handle, 0x84, IntPtr.Zero, (IntPtr)((y << 16) | (x & 0xffff)));
            if (hit == (IntPtr)1) opaque ??= new Point(x, y);
            if (hit == (IntPtr)(-1)) transparent ??= new Point(x, y);
        }
        check(opaque is not null && transparent is not null, "외부 캐릭터의 실제 그림만 클릭 가능하고 여백은 클릭 통과");
        var click = outside.PointToClient(opaque!.Value);
        MonitorForm.SendMessage(outside.Handle, 0x201, (IntPtr)1, (IntPtr)((click.Y << 16) | (click.X & 0xffff)));
        click = outside.PointToClient(opaque.Value);
        MonitorForm.SendMessage(outside.Handle, 0x202, IntPtr.Zero, (IntPtr)((click.Y << 16) | (click.X & 0xffff)));
        check(form.CharacterVisitActive, "외부 캐릭터 클릭으로 가까이 보기 시작");
        form.PreviewOrange(.08, 3.1);
        form.SetCharacterDisplay(CharacterDisplay.Inside);
        using (var hiddenWalker = new OrangeWalker())
        {
            var design = form.DesignSize;
            float margin = hiddenWalker.PoseAt(0, design).Center.X;
            double distance = design.Width - margin * 2 + (design.Height - margin * 2) / 2;
            form.PreviewOrange(distance / hiddenWalker.Perimeter(design), 3.1);
            var pose = hiddenWalker.PoseAt(distance, design);
            var button = form.ButtonBounds(8);
            Point? covered = null;
            for (int y = (int)button.Top + 1; y < button.Bottom - 1 && covered is null; y++)
            for (int x = (int)button.Left + 1; x < button.Right - 1 && covered is null; x++)
                if (hiddenWalker.HitTestSprite(new PointF(x, y), pose.Center, pose.Angle, 3.1, form.OrangeHeight)) covered = new Point(x, y);
            check(covered is not null, "메뉴 버튼 아래에 캐릭터가 겹치는 조건 재현");
            MonitorForm.SendMessage(form.Handle, 0x201, (IntPtr)1, (IntPtr)((covered!.Value.Y << 16) | covered.Value.X));
            check(form.ToolsExpanded && !form.CharacterVisitActive, "가려진 캐릭터 대신 화면에 보이는 메뉴 버튼이 클릭됨");
            if (form.ToolsExpanded) form.ToggleTools();
        }
        form.SetCharacterDisplay(CharacterDisplay.Outside);
        form.PreviewOrange(.08, 3.1);
        var foreground = GetForegroundWindow();
        Point before = outside.Location;
        form.Location = new Point(form.Left + 8, form.Top + 6);
        check(outside.Location == new Point(before.X + 8, before.Y + 6) && GetForegroundWindow() == foreground, "이동 시 외부 캐릭터 동행과 포커스 유지");
        int oldWidth = outside.Width;
        form.Size = new Size(original.Width * 2, original.Height * 2);
        double actualScale = form.ClientSize.Width / (double)original.Width;
        check(Math.Abs(outside.Width - ((oldWidth - 2) * actualScale + 2)) <= 1, "화면 제한을 반영한 실제 창 배율에 맞춰 외부 캐릭터도 확대");
        int scaledWidth = outside.Width;
        form.ChangeOrangeSize(20);
        check(outside.Width > scaledWidth, "기존 크기 버튼으로 외부 캐릭터도 확대");
        form.SelectCharacter(1);
        check(form.SelectedCharacter == 1 && outside.Visible, "캐릭터 변경 시 안팎에 공통 선택 적용");
        check(form.CharacterVisitActive, "캐릭터 변경 시 얼굴 가까이 보기 시작");
        check(!form.BeginCharacterVisit(), "연출 중 재클릭은 무시");
        form.PreviewOrange(.1, 7.1);
        check(!form.CharacterVisitActive && outside.Visible, "확대 연출 종료 후 원래 외부 모드로 복귀");
        form.SetCharacterDisplay(CharacterDisplay.Inside);
        form.PreviewOrange(.1, 0);
        using (var duck = new OrangeWalker(OrangeWalker.Characters[1], form.OrangeHeight))
        {
            var pose = duck.PoseAt(.1 * duck.Perimeter(form.DesignSize), form.DesignSize);
            PointF? hit = null;
            for (int y = (int)pose.Bounds.Top; y < pose.Bounds.Bottom && hit is null; y++)
            for (int x = (int)pose.Bounds.Left; x < pose.Bounds.Right && hit is null; x++)
                if (duck.HitTestSprite(new PointF(x, y), pose.Center, pose.Angle, 0, form.OrangeHeight)) hit = new PointF(x, y);
            check(hit is not null && !duck.HitTestSprite(new PointF(-100, -100), pose.Center, pose.Angle, 0, form.OrangeHeight),
                "캐릭터 실제 픽셀만 클릭 감지하고 투명 여백 제외");
            var point = hit!.Value;
            float scale = form.ClientSize.Width / (float)form.DesignSize.Width;
            MonitorForm.SendMessage(form.Handle, 0x201, (IntPtr)1,
                (IntPtr)(((int)(point.Y * scale) << 16) | ((int)(point.X * scale) & 0xffff)));
            MonitorForm.SendMessage(form.Handle, 0x202, IntPtr.Zero,
                (IntPtr)(((int)(point.Y * scale) << 16) | ((int)(point.X * scale) & 0xffff)));
            check(form.CharacterVisitActive, "내부 캐릭터 클릭으로 가까이 보기 시작");
            var origin = form.PointToScreen(Point.Empty);
            var center = new PointF(origin.X + pose.Center.X * scale, origin.Y + pose.Center.Y * scale);
            PointF RenderedCenter() => (PointF)typeof(OutsideCharacterForm).GetField("hitCenter", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(outside)!;
            float RenderedSize() => (float)typeof(OutsideCharacterForm).GetField("hitSize", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(outside)!;
            check(Math.Abs(RenderedCenter().X - center.X) <= 1 && Math.Abs(RenderedCenter().Y - center.Y) <= 1,
                "내부 클릭 확대는 화면 밖 이동 없이 클릭한 캐릭터 중심에서 시작");
            form.PreviewOrange(.1, .05);
            var expected = CharacterVisit.PoseAt(.05, center, form.OrangeHeight * scale, pose.Angle,
                Screen.FromPoint(Point.Round(center)).WorkingArea, duck.Face);
            check(Math.Abs(RenderedCenter().X - expected.Center.X) <= 1 &&
                Math.Abs(RenderedCenter().Y - expected.Center.Y) <= 1 && RenderedSize() > form.OrangeHeight * scale,
                "내부 클릭 직후 원래 위치에서 크기와 중심이 함께 확대 경로로 이동");
            form.PreviewOrange(.1, CharacterVisit.Duration);
            check(!form.CharacterVisitActive && !outside.Visible, "내부 클릭 2.05초 후 원래 내부 표시로 정확히 복귀");
            form.BeginCharacterVisit();
        }
        form.HideToTray();
        check(!form.CharacterVisitActive && !outside.Visible, "숨김 중 가까이 보기 취소와 외부 표시 제거");
        form.RestoreFromTray();
        form.SetCharacterDisplay(CharacterDisplay.None);
        form.SelectCharacter(0);
        check(!form.CharacterVisitActive && !outside.Visible, "캐릭터 끔에서는 변경·클릭 연출 안 함");
        form.SetCharacterDisplay(CharacterDisplay.Outside);
        form.TogglePin();
        check(outside.TopMost == form.TopMost, "핀 해제 시 외부 고정도 해제");
        form.TogglePin();
        check(outside.TopMost && form.TopMost, "핀 설정 시 외부도 함께 고정");
        using var process = System.Diagnostics.Process.GetCurrentProcess();
        int handles = GetGuiResources(process.Handle, 0);
        for (int i = 0; i < 200; i++) form.PreviewOrange(i / 200d, i * .033);
        check(GetGuiResources(process.Handle, 0) <= handles + 2, "반복 렌더링 중 GDI 자원 누적 없음");
        form.HideToTray();
        check(!outside.Visible, "트레이 숨김 시 외부 캐릭터도 숨김");
        form.RestoreFromTray();
        check(outside.Visible, "다시 표시하면 외부 캐릭터도 복원");
        form.SetCharacterDisplay(CharacterDisplay.FreeRoam);
        form.PreviewOrange(.1, 10);
        var roamingBounds = outside.Bounds;
        check(form.BeginCharacterVisit(fromOutside: true), "자유 이동 중 클릭하면 확대 연출 시작");
        form.PreviewOrange(.1, 13.1);
        check(!form.CharacterVisitActive && outside.Bounds == roamingBounds, "자유 이동 확대 후 클릭한 위치에 정확히 복귀");
        form.SetCharacterDisplay(CharacterDisplay.FollowMouse);
        MonitorForm.SendMessage(outside.Handle, 0x201, (IntPtr)1, (IntPtr)((outside.Height / 2 << 16) | (outside.Width / 2)));
        check(!outside.ClickThrough && outside.IsPointerCaptured && (GetWindowLong(outside.Handle, -20) & 0x20) == 0 && !form.CharacterVisitActive,
            "마우스 따라가기에서도 몸은 잡을 수 있고 누르는 중 확대 연출 미실행");
        outside.CancelPointer();
        check(form.BeginCharacterVisit(fromOutside: true), "마우스 따라가기 모드도 일반 짧은 클릭 확대 허용");
        form.SetCharacterDisplay(CharacterDisplay.FollowMouse);
        var roaming = (CharacterRoaming)typeof(MonitorForm).GetField("roaming", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(form)!;
        var simulationArea = new Rectangle(-1000, -1000, 3000, 3000);
        roaming.Reset(new PointF(100, 100));
        roaming.Step(1d / 60, simulationArea, 48, new PointF(130, 100));
        CharacterEffect CurrentEffect() => (CharacterEffect)typeof(MonitorForm)
            .GetProperty("CurrentCharacterEffect", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(form)!;
        check(settings.CharacterDisplay == CharacterDisplay.FollowMouse && CurrentEffect() == CharacterEffect.Arrived,
            "커서 조작 없이 합성 도착 입력에서 Follow 모드를 유지하고 즉시 음표 효과 연결");
        roaming.Step(1d / 60, simulationArea, 48, new PointF(600, 100));
        check(settings.CharacterDisplay == CharacterDisplay.FollowMouse && CurrentEffect() == CharacterEffect.Startled,
            "커서 조작 없이 합성 재추적 입력에서 음표 해제하고 기존 느낌표 효과 복원");
        form.Close();
        check(outside.IsDisposed, "본체 종료 시 외부 창과 자원 정리");
        CheckAllDisabled(directory, check);
    }

    private static void CheckAllDisabled(string directory, Action<bool, string> check)
    {
        using var codex = new UsageSource(false);
        using var claude = new UsageSource(true);
        using var gemini = new UsageSource(ServiceKind.Gemini);
        var settings = new MonitorSettings { CodexEnabled = false, ClaudeEnabled = false, GeminiEnabled = false,
            VisualEffects = false, CharacterDisplay = CharacterDisplay.None };
        using var form = new MonitorForm(codex, claude, false, settings, gemini);
        form.Show();
        Application.DoEvents();
        using var blank = new Bitmap(form.Width, form.Height);
        form.DrawToBitmap(blank, form.ClientRectangle);
        bool emptyPanel = true;
        for (int y = 6; y < blank.Height - 6; y++)
        for (int x = 6; x < 250; x++) emptyPanel &= blank.GetPixel(x, y).ToArgb() == form.BackColor.ToArgb();
        check(emptyPanel, "모든 AI 비활성 합성 렌더에서 안내 문구 없이 서비스 영역이 비어 있음");
        form.SetCharacterDisplay(CharacterDisplay.Inside);
        form.PreviewOrange(.08, 0);
        using var character = new Bitmap(form.Width, form.Height);
        form.DrawToBitmap(character, form.ClientRectangle);
        bool visibleCharacter = false;
        for (int y = 6; y < character.Height - 6; y++)
        for (int x = 6; x < 250; x++) visibleCharacter |= character.GetPixel(x, y) != blank.GetPixel(x, y);
        check(visibleCharacter, "모든 AI 비활성 상태에서도 내부 캐릭터가 표시됨");
        character.Save(Path.Combine(directory, "all-services-disabled.png"));
        var collapsed = form.Size;
        form.CreateTray();
        var menu = (ContextMenuStrip)typeof(MonitorForm).GetField("trayMenu", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(form)!;
        menu.Items.OfType<ToolStripMenuItem>().Single(item => item.Text is string text && text.StartsWith(codex.Name)).PerformClick();
        check(codex.Enabled && settings.CodexEnabled && form.Width > collapsed.Width,
            "모든 AI 비활성 뒤에도 설정 메뉴에서 Codex를 다시 켜고 서비스 패널 복원");
        form.Close();
    }
}
