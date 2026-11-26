using UsageMonitor;
using System.Drawing.Imaging;
using System.Reflection;
using System.Runtime.InteropServices;

internal static class CharacterInteractionChecks
{
    internal static void Run(string directory, Action<bool, string> check)
    {
        CheckSettings(directory, check);
        CheckBodyBounds(check);
        using var walker = new OrangeWalker();
        CheckEffects(walker, check);
        CheckArrivalNote(walker, check);
        CheckCapture(walker, check);
    }

    private static void CheckArrivalNote(OrangeWalker walker, Action<bool, string> check)
    {
        var effect = CharacterEffect.Arrived;
        foreach (var size in new[] { 24f, 104f, 200f })
        {
            var center = new PointF(150, 170);
            using var normal = new Bitmap(400, 400);
            using var arrived = new Bitmap(400, 400);
            using (var g = Graphics.FromImage(normal)) walker.DrawSprite(g, center, 0, 0, size);
            using (var g = Graphics.FromImage(arrived)) walker.DrawSprite(g, center, 0, 0, size, effect: effect);
            var sprite = CharacterVisual.SpriteBounds(center, size, 0, effect);
            var visual = CharacterVisual.Bounds(center, size, 0, effect);
            bool bodyUnchanged = true, clickThrough = true, contained = true;
            int notePixels = 0;
            int minX = 400, maxX = 0, minY = 400, maxY = 0;
            for (int y = 0; y < arrived.Height; y++)
            for (int x = 0; x < arrived.Width; x++)
            {
                if (sprite.Contains(x + .5f, y + .5f)) bodyUnchanged &= normal.GetPixel(x, y) == arrived.GetPixel(x, y);
                if (normal.GetPixel(x, y) == arrived.GetPixel(x, y)) continue;
                notePixels++;
                minX = Math.Min(minX, x); maxX = Math.Max(maxX, x);
                minY = Math.Min(minY, y); maxY = Math.Max(maxY, y);
                clickThrough &= !walker.HitTestSprite(new PointF(x + .5f, y + .5f), center, 0, 0, size, effect: effect);
                contained &= RectangleF.Inflate(visual, 1, 1).Contains(x + .5f, y + .5f);
            }
            check(CharacterVisual.Stretch(effect) == 1 && bodyUnchanged, size + "px 도착 음표는 원래 몸 크기와 픽셀 유지");
            check(notePixels >= 25 && maxX - minX > (maxY - minY) * .5f, size + "px 음표는 작은 몸에서도 느낌표와 구분되는 너비로 표시");
            check(contained && clickThrough, size + "px 음표 전체가 렌더 영역 안에 있고 클릭은 통과");
            if (size == 24) arrived.Save(Path.Combine(AppContext.BaseDirectory, "character-arrival-note.png"));
        }
    }

    private static void CheckBodyBounds(Action<bool, string> check)
    {
        foreach (var character in OrangeWalker.Characters)
        {
            using var walker = new OrangeWalker(character);
            var frames = (Bitmap[])typeof(OrangeWalker).GetField("frames", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(walker)!;
            int left = frames[0].Width, top = frames[0].Height, right = 0, bottom = 0;
            foreach (var frame in frames)
            {
                var locked = frame.LockBits(new Rectangle(Point.Empty, frame.Size), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
                var pixels = new int[locked.Stride / 4 * locked.Height];
                Marshal.Copy(locked.Scan0, pixels, 0, pixels.Length);
                frame.UnlockBits(locked);
                for (int y = 0; y < frame.Height; y++)
                for (int x = 0; x < frame.Width; x++)
                    if (((uint)pixels[y * (locked.Stride / 4) + x] >> 24) >= 32)
                    { left = Math.Min(left, x); top = Math.Min(top, y); right = Math.Max(right, x + 1); bottom = Math.Max(bottom, y + 1); }
            }
            var body = walker.NormalizedBodyBounds;
            check(Math.Abs(body.Left * frames[0].Width - left) < .001 && Math.Abs(body.Top * frames[0].Height - top) < .001 &&
                Math.Abs(body.Right * frames[0].Width - right) < .001 && Math.Abs(body.Bottom * frames[0].Height - bottom) < .001,
                character.Name + " 실제 알파 픽셀 공통 몸 영역과 거리 계산 경계 일치");
        }
    }

    private static void CheckSettings(string directory, Action<bool, string> check)
    {
        string path = Path.Combine(directory, "character-interaction-settings.json");
        File.WriteAllText(path, "{\"CharacterDisplay\":3,\"CodexEnabled\":false}");
        var legacy = MonitorSettings.Load(path);
        check(legacy.CharacterDisplay == CharacterDisplay.Inside && !legacy.CodexEnabled,
            "구버전 내부+외부 설정은 다른 서비스 선택을 보존하고 내부만으로 이전");
        check(legacy.CharacterDesktopX is null && legacy.CharacterDesktopY is null && legacy.CharacterTrackDistance == 0,
            "이전 설정에 위치가 없으면 기본 배치를 유지");
        check(legacy.CharacterDesktopMovement == CharacterDisplay.FreeRoam && new MonitorSettings().CharacterDesktopMovement == CharacterDisplay.FreeRoam,
            "바탕 이동 방식이 없는 구버전 내부 설정과 새 설정은 자유 이동이 기본");
        foreach (var mode in new[] { CharacterDisplay.FreeRoam, CharacterDisplay.FollowMouse })
        {
            File.WriteAllText(path, "{\"CharacterDisplay\":" + (int)mode + "}");
            check(MonitorSettings.Load(path).CharacterDesktopMovement == mode, "구버전 명시 " + mode + " 선택은 바탕 이동 방식으로 이전");
        }
        File.WriteAllText(path, "{\"CharacterDisplay\":1,\"CharacterDesktopMovement\":8}");
        check(MonitorSettings.Load(path).CharacterDesktopMovement == CharacterDisplay.FollowMouse,
            "내부 표시로 저장되어도 별도 기억한 추적 방식 보존");
        File.WriteAllText(path, "{\"CharacterDesktopMovement\":3}");
        check(MonitorSettings.Load(path).CharacterDesktopMovement == CharacterDisplay.FreeRoam,
            "유효하지 않은 바탕 이동 방식은 기본 자유 이동으로 복원");
        var saved = new MonitorSettings { CharacterDisplay = CharacterDisplay.FollowMouse, CharacterTrackDistance = .3215,
            CharacterDesktopX = -250.25f, CharacterDesktopY = 370.5f, CharacterFacingLeft = true };
        saved.Save(path);
        var loaded = MonitorSettings.Load(path);
        check(loaded.CharacterDisplay == CharacterDisplay.FollowMouse && loaded.CharacterTrackDistance == .3215 &&
            loaded.CharacterDesktopX == -250.25f && loaded.CharacterDesktopY == 370.5f && loaded.CharacterFacingLeft,
            "붙는 위치·음수 모니터 좌표·바라보는 방향을 설정에 저장하고 복원");
    }

    private static void CheckEffects(OrangeWalker walker, Action<bool, string> check)
    {
        const int side = 360;
        const float size = 140;
        var center = new PointF(145, 195);
        using var normal = new Bitmap(side, side);
        using var startled = new Bitmap(side, side);
        using var grabbed = new Bitmap(side, side);
        using (var g = Graphics.FromImage(normal)) walker.DrawSprite(g, center, 0, 0, size);
        using (var g = Graphics.FromImage(startled)) walker.DrawSprite(g, center, 0, 0, size, effect: CharacterEffect.Startled);
        using (var g = Graphics.FromImage(grabbed)) walker.DrawSprite(g, center, 0, 0, size, effect: CharacterEffect.None);
        var original = CharacterVisual.SpriteBounds(center, size, 0, CharacterEffect.None);
        var stretched = CharacterVisual.SpriteBounds(center, size, 0, CharacterEffect.Startled);
        check(Math.Abs(stretched.Height - original.Height * 1.06f) < .001f && stretched.Bottom == original.Bottom &&
            stretched.Width == original.Width, "놀람은 발과 가로 폭을 고정하고 세로만 정확히 6% 늘림");
        bool unchangedBody = true, changed = false, hitAligned = true, insideVisual = true;
        var visual = CharacterVisual.Bounds(center, size, 0, CharacterEffect.Startled);
        for (int y = 0; y < side; y++)
        for (int x = 0; x < side; x++)
        {
            if (original.Contains(x + .5f, y + .5f))
            {
                unchangedBody &= normal.GetPixel(x, y) == grabbed.GetPixel(x, y);
                changed |= normal.GetPixel(x, y) != startled.GetPixel(x, y);
            }
            if (startled.GetPixel(x, y).A > 0) insideVisual &= RectangleF.Inflate(visual, 1, 1).Contains(x + .5f, y + .5f);
            var point = new PointF(x + .37f, y + .41f);
            var transformed = new PointF(point.X, original.Bottom + (point.Y - original.Bottom) * 1.06f);
            hitAligned &= walker.HitTestSprite(point, center, 0, 0, size) ==
                walker.HitTestSprite(transformed, center, 0, 0, size, effect: CharacterEffect.Startled);
        }
        check(changed && hitAligned, "6% 세로 변형된 실제 스프라이트에 클릭 좌표 역변환 적용");
        check(unchangedBody, "드래그 자체에는 별도 몸 변형이나 느낌표를 추가하지 않음");
        check(CharacterVisual.Stretch(CharacterEffect.Startled, 0) == 1 &&
            Math.Abs(CharacterVisual.Stretch(CharacterEffect.Startled, .25f) - 1.06f) < .001 &&
            CharacterVisual.Stretch(CharacterEffect.Startled, .75f) < 1.03f && CharacterVisual.Stretch(CharacterEffect.Startled, 1) == 1,
            "공통 반응은 0.2초 안에 발고정 6% pop 후 원래 크기로 shrink");
        using (var large = new Bitmap(300, 50))
        using (var g = Graphics.FromImage(large))
        {
            var clip = new RectangleF(0, 0, large.Width, large.Height);
            walker.DrawSprite(g, new PointF(150, 25), 0, 0, 240, effect: CharacterEffect.Startled, alertClip: clip);
            var alertInside = CharacterVisual.AlertBounds(CharacterVisual.SpriteBounds(new PointF(150, 25), 240, 0, CharacterEffect.Startled), 240, clip);
            check(clip.Contains(alertInside) && alertInside.Height <= 48,
                "큰 내부 몸의 느낌표도 실제 창 크기 안에서 잘리지 않게 표시");
        }
        var alert = CharacterVisual.AlertBounds(stretched, size);
        check(!walker.HitTestSprite(new PointF(alert.Left + alert.Width / 2, alert.Top + 2), center, 0, 0, size,
            effect: CharacterEffect.Startled), "느낌표는 몸 픽셀 히트 영역에 포함하지 않음");
        check(insideVisual, "늘어난 머리와 우측 위 느낌표 모두 시각 영역에 포함되어 잘리지 않음");
        bool fits = true;
        var screen = new Rectangle(-1920, -240, 1920, 1080);
        foreach (var effect in new[] { CharacterEffect.Startled, CharacterEffect.Arrived, CharacterEffect.None })
        foreach (float angle in new[] { 0f, 90f, 180f, 270f })
        foreach (var corner in new[] { new PointF(screen.Left, screen.Top), new PointF(screen.Right, screen.Top),
            new PointF(screen.Left, screen.Bottom), new PointF(screen.Right, screen.Bottom) })
        {
            var fitted = CharacterVisual.FitCenter(corner, size, angle, effect, screen);
            var fittedVisual = CharacterVisual.Bounds(fitted, size, angle, effect);
            fits &= fittedVisual.Left >= screen.Left - .001f && fittedVisual.Right <= screen.Right + .001f &&
                fittedVisual.Top >= screen.Top - .001f && fittedVisual.Bottom <= screen.Bottom + .001f;
        }
        check(fits && CharacterVisual.FitCenter(center, size, 0, CharacterEffect.None, new Rectangle(0, 0, side, side)) == center,
            "음수 모니터의 네 모서리에서도 느낌표·늘림 전체를 화면 안에 맞추고 내부 위치는 유지");
        using var preview = new Bitmap(side * 3, side);
        using (var g = Graphics.FromImage(preview))
        {
            g.Clear(Color.FromArgb(45, 50, 60));
            g.DrawImageUnscaled(normal, 0, 0);
            g.DrawImageUnscaled(startled, side, 0);
            g.DrawImageUnscaled(grabbed, side * 2, 0);
        }
        preview.Save(Path.Combine(AppContext.BaseDirectory, "character-interaction-effects.png"));
    }

    private static void CheckCapture(OrangeWalker walker, Action<bool, string> check)
    {
        using var owner = new Form { Opacity = 0, ShowInTaskbar = false };
        owner.Show();
        using var layer = new OutsideCharacterForm();
        var area = Screen.PrimaryScreen!.WorkingArea;
        var center = new PointF(area.Left + area.Width / 2f, area.Top + area.Height / 2f);
        layer.UpdatePose(owner, walker, center, 100, 0, 0, area, smooth: true);
        var pressed = new List<Point>(); var moved = new List<Point>(); var released = new List<Point>();
        int canceled = 0;
        layer.PointerPressed += pressed.Add;
        layer.PointerMoved += moved.Add;
        layer.PointerReleased += released.Add;
        layer.PointerCanceled += () => canceled++;
        Point? opaque = null, transparent = null;
        for (int y = layer.Top; y < layer.Bottom && (opaque is null || transparent is null); y++)
        for (int x = layer.Left; x < layer.Right && (opaque is null || transparent is null); x++)
        {
            var screen = new Point(x, y);
            if (walker.HitTestSprite(screen, center, 0, 0, 100)) opaque ??= screen;
            else transparent ??= screen;
        }
        IntPtr Pack(Point point) => (IntPtr)((point.Y << 16) | (point.X & 0xffff));
        void Down(Point screen) => MonitorForm.SendMessage(layer.Handle, 0x201, (IntPtr)1, Pack(layer.PointToClient(screen)));
        Down(transparent!.Value);
        check(pressed.Count == 0 && !layer.IsPointerCaptured && !layer.Capture, "투명 여백을 누르면 드래그와 마우스 캡처를 시작하지 않음");
        Down(opaque!.Value);
        check(pressed.Count == 1 && pressed[0] == opaque && layer.IsPointerCaptured && layer.Capture,
            "실제 몸 픽셀을 누르면 화면 좌표 전달과 마우스 캡처 시작");
        var outside = new Point(layer.Left - 40, layer.Top - 30);
        MonitorForm.SendMessage(layer.Handle, 0x200, (IntPtr)1, Pack(layer.PointToClient(outside)));
        MonitorForm.SendMessage(layer.Handle, 0x202, IntPtr.Zero, Pack(layer.PointToClient(outside)));
        check(moved.LastOrDefault() == outside && released.Count == 1 && released[0] == outside &&
            !layer.IsPointerCaptured && !layer.Capture && canceled == 0, "몸 밖까지 끌어도 이동·놓기 좌표를 전달하고 정상 캡처 해제");
        Down(opaque.Value); layer.Capture = false;
        check(canceled == 1 && !layer.IsPointerCaptured, "다른 창이 캡처를 가져가면 드래그 취소");
        Down(opaque.Value); MonitorForm.SendMessage(layer.Handle, 0x100, (IntPtr)0x1B, IntPtr.Zero);
        check(canceled == 2 && !layer.IsPointerCaptured && !layer.Capture, "ESC로 캡처와 드래그 상태를 함께 취소");
        Down(opaque.Value); layer.Hide();
        check(canceled == 3 && !layer.IsPointerCaptured && !layer.Capture, "창이 숨겨지면 캡처와 드래그 상태를 함께 취소");
        layer.UpdatePose(owner, walker, center, 100, 0, 0, area, smooth: true, effect: CharacterEffect.Startled);
        var visual = CharacterVisual.Bounds(center, 100, 0, CharacterEffect.Startled);
        check(layer.Bounds.Contains(Rectangle.Ceiling(visual)), "실제 투명 창도 늘어난 몸과 느낌표를 포함하는 크기로 확장");
        var alert = CharacterVisual.AlertBounds(CharacterVisual.SpriteBounds(center, 100, 0, CharacterEffect.Startled), 100);
        var alertPoint = Point.Round(new PointF(alert.Left + alert.Width / 2, alert.Top + 2));
        check(MonitorForm.SendMessage(layer.Handle, 0x84, IntPtr.Zero, Pack(alertPoint)) == (IntPtr)(-1),
            "실제 느낌표 창 픽셀 위에서도 클릭을 아래 창으로 통과");
        layer.UpdatePose(owner, walker, center, 100, 0, 0, area, smooth: true, effect: CharacterEffect.Arrived);
        var noteVisual = CharacterVisual.Bounds(center, 100, 0, CharacterEffect.Arrived);
        var note = CharacterVisual.NoteBounds(CharacterVisual.SpriteBounds(center, 100, 0, CharacterEffect.Arrived), 100);
        var notePoint = Point.Round(new PointF(note.Left + note.Width * .47f, note.Top + note.Height * .3f));
        check(layer.Bounds.Contains(Rectangle.Ceiling(noteVisual)), "실제 투명 창이 도착 음표의 전체 시각 영역 포함");
        check(MonitorForm.SendMessage(layer.Handle, 0x84, IntPtr.Zero, Pack(notePoint)) == (IntPtr)(-1),
            "실제 도착 음표 줄기 위에서도 클릭을 아래 창으로 통과");
    }
}
