using System.Reflection;
using UsageMonitor;

internal static class CharacterHoverChecks
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    internal static void Run(Action<bool, string> check)
    {
        using var codex = new UsageSource(false);
        using var claude = new UsageSource(true);
        var settings = new MonitorSettings { VisualEffects = false };
        using var form = new MonitorForm(codex, claude, false, settings);
        form.Show();
        form.Location = new Point(100, 150);
        T Field<T>(string name) => (T)typeof(MonitorForm).GetField(name, Private)!.GetValue(form)!;
        T Property<T>(string name) => (T)typeof(MonitorForm).GetProperty(name, Private)!.GetValue(form)!;
        void Invoke(string name, params object[] args) => typeof(MonitorForm).GetMethod(name, Private)!.Invoke(form, args);
        void Advance(double time, Point pointer) => Invoke("AdvanceCharacter", time, pointer);
        var away = new Point(-100000, -100000);
        var walker = Field<OrangeWalker>("orange");
        var roam = Field<CharacterRoaming>("roaming");
        bool Desktop() => settings.CharacterDisplay is CharacterDisplay.FreeRoam or CharacterDisplay.FollowMouse;
        OutsideCharacterForm Overlay() => form.OwnedForms.OfType<OutsideCharacterForm>().Single();
        bool Hit(Point screen)
        {
            if (Desktop() || settings.CharacterDisplay == CharacterDisplay.Outside) return Overlay().ContainsBody(screen);
            var local = form.PointToClient(screen);
            float scale = form.ClientSize.Width / (float)form.DesignSize.Width;
            return form.ClientRectangle.Contains(local) && (bool)typeof(MonitorForm).GetMethod("HitInsideCharacter", Private)!
                .Invoke(form, new object[] { new PointF(local.X / scale, local.Y / scale) })!;
        }
        Point BodyPoint()
        {
            var bounds = Desktop() || settings.CharacterDisplay == CharacterDisplay.Outside ? Overlay().Bounds : form.Bounds;
            for (int y = bounds.Top + 2; y < bounds.Bottom - 2; y++)
            for (int x = bounds.Left + 2; x < bounds.Right - 2; x++)
            {
                var point = new Point(x, y);
                if (Hit(point) && Hit(new Point(x - 2, y)) && Hit(new Point(x + 2, y)) &&
                    Hit(new Point(x, y - 2)) && Hit(new Point(x, y + 2))) return point;
            }
            throw new InvalidOperationException("hover 검사용 실제 몸 픽셀 없음");
        }
        double FrameTime() => Desktop() ? roam.AnimationSeconds : Field<double>("characterAnimationTime");
        PointF Position() => Desktop() ? roam.Position : walker.PoseAt(Field<double>("orangeDistance"), form.DesignSize).Center;
        foreach (var mode in new[] { CharacterDisplay.Inside, CharacterDisplay.Outside, CharacterDisplay.FreeRoam, CharacterDisplay.FollowMouse })
        {
            form.SetCharacterDisplay(mode);
            form.PreviewOrange(.08, 0);
            var body = BodyPoint();
            Invoke("UpdateCharacterHover", body);
            var position = Position();
            double frameTime = FrameTime();
            Advance(.05, body);
            Advance(.3, body);
            Advance(3.3, body);
            check(Field<bool>("characterHovered") && Position() == position && FrameTime() == frameTime,
                mode + " 몸 hover 3.3초 동안 위치와 걷기 프레임 시간 정지");
            check(Property<CharacterEffect>("CurrentCharacterEffect") == CharacterEffect.Startled &&
                Property<float>("CurrentCharacterReactionProgress") == 1,
                mode + " 첫 pop 후 몸은 정상 크기로 돌아오고 느낌표는 계속 유지");
            if (mode != CharacterDisplay.Inside)
            {
                Overlay().RefreshBodyHover(body);
                check(Field<bool>("characterHovered"), mode + " 알파 창 경계 변경의 leave 재검사도 실제 몸 위면 정지 유지");
                Overlay().RefreshBodyHover(away);
            }
            Advance(3.333, away);
            var resumed = Position();
            double distance = Math.Sqrt(Math.Pow(resumed.X - position.X, 2) + Math.Pow(resumed.Y - position.Y, 2));
            check(!Field<bool>("characterHovered") && distance > 0 && distance < 25 && FrameTime() > frameTime,
                mode + " 커서를 떼면 누적 3.3초 점프 없이 다음 프레임부터 재개");
        }
        foreach (var mode in new[] { CharacterDisplay.Inside, CharacterDisplay.Outside, CharacterDisplay.FreeRoam, CharacterDisplay.FollowMouse })
        {
            form.SetCharacterDisplay(mode);
            form.PreviewOrange(.08, 0);
            PointF future;
            float size = form.OrangeHeight * form.ClientSize.Width / form.DesignSize.Width;
            float angle;
            if (Desktop())
            {
                roam.Relocate(new PointF(500, 350), false);
                typeof(CharacterRoaming).GetField("target", Private)!.SetValue(roam, new PointF(750, 350));
                typeof(CharacterRoaming).GetField("speed", Private)!.SetValue(roam, 20f);
                if (mode == CharacterDisplay.FollowMouse) roam.RequireFollowApproach();
                Invoke("UpdateOutsideCharacter", new object?[] { null }!);
                future = new PointF(510, 350);
                angle = 0;
            }
            else
            {
                bool outer = mode == CharacterDisplay.Outside;
                double distance = Field<double>("orangeDistance");
                if (outer) distance = distance / walker.Perimeter(form.DesignSize) * walker.Perimeter(form.DesignSize, true);
                var pose = walker.PoseAt(distance + 10, form.DesignSize, outer);
                var origin = form.PointToScreen(Point.Empty);
                float scale = form.ClientSize.Width / (float)form.DesignSize.Width;
                future = new PointF(origin.X + pose.Center.X * scale, origin.Y + pose.Center.Y * scale);
                angle = pose.Angle;
            }
            Point? stationary = null;
            for (int y = (int)(future.Y - size / 2) + 3; y < future.Y + size / 2 - 3 && stationary is null; y++)
            for (int x = (int)(future.X - size / 2) + 3; x < future.X + size / 2 - 3 && stationary is null; x++)
                if (!Hit(new Point(x, y)) && walker.HitTestSprite(new PointF(x, y), future, angle, .25, size) &&
                    walker.HitTestSprite(new PointF(x - 2, y), future, angle, .25, size) &&
                    walker.HitTestSprite(new PointF(x + 2, y), future, angle, .25, size)) stationary = new Point(x, y);
            if (stationary is not Point cursor) { check(false, mode + " 정지 커서 진입 재현 픽셀 준비"); continue; }
            for (int tick = 1; tick <= 60 && !Field<bool>("characterHovered"); tick++) Advance(tick / 60d, cursor);
            var position = Position();
            double frameTime = FrameTime();
            Advance(2, cursor);
            check(Field<bool>("characterHovered") && Position() == position && FrameTime() == frameTime,
                mode + " 커서를 움직이지 않아도 캐릭터가 몸 아래로 걸어들어오면 정지");
        }
        form.SetCharacterDisplay(CharacterDisplay.Inside);
        form.PreviewOrange(.08, 0);
        var pressed = BodyPoint();
        Invoke("UpdateCharacterHover", pressed);
        Advance(1, pressed);
        check(form.BeginCharacterVisit() && !Field<bool>("characterHovered") && Property<CharacterEffect>("CurrentCharacterEffect") == CharacterEffect.None,
            "hover 정지보다 클릭 확대를 우선하고 느낌표는 숨김");
        check(Field<double>("visitAnimationTime") == 0, "오래 hover한 뒤 확대해도 멈춘 몸 프레임에서 시작");
        Advance(1.3, pressed);
        check(form.CharacterVisitActive && !Field<bool>("characterHovered"), "확대 중 몸 히트가 hover 정지를 다시 만들지 않음");
        form.SetCharacterDisplay(CharacterDisplay.FreeRoam);
        form.SetCharacterDisplay(CharacterDisplay.Inside);
        form.PreviewOrange(.08, 0);
        pressed = BodyPoint();
        Invoke("UpdateCharacterHover", pressed);
        Invoke("BeginCharacterPress", pressed, false);
        var drop = new Point(form.Left + form.Width / 2, form.Bottom + 180);
        Invoke("MoveCharacterPress", drop);
        check(Field<bool>("draggingCharacter") && !Field<bool>("characterHovered") && Property<CharacterEffect>("CurrentCharacterEffect") == CharacterEffect.None,
            "hover 정지보다 잡기·드래그를 우선하고 느낌표는 숨김");
        Invoke("EndCharacterPress", drop);
        check(settings.CharacterDisplay == CharacterDisplay.FreeRoam && !Field<bool>("characterHovered"), "드롭에 hover 잠금이 남지 않고 기억한 이동 모드 유지");
        form.SetCharacterDisplay(CharacterDisplay.FollowMouse);
        form.PreviewOrange(.08, 0);
        roam.Reset(new PointF(500, 350));
        roam.Step(.016, new Rectangle(0, 0, 1500, 900), form.OrangeHeight, new PointF(510, 350));
        Invoke("UpdateOutsideCharacter", new object?[] { null }!);
        var noteBody = BodyPoint();
        Invoke("UpdateCharacterHover", noteBody);
        Advance(1, noteBody);
        check(Property<CharacterEffect>("CurrentCharacterEffect") == CharacterEffect.Startled && Property<float>("CurrentCharacterReactionProgress") == 1,
            "도착 음표와 겹치는 hover는 몸 정상 크기의 유지 느낌표를 우선");
        Advance(1.033, away);
        check(!Field<bool>("characterHovered") && !roam.Nearby && Property<CharacterEffect>("CurrentCharacterEffect") != CharacterEffect.Arrived,
            "도착 hover를 벗어나 재추적할 때 이전 음표가 되살아나지 않음");
        form.SetCharacterDisplay(CharacterDisplay.FreeRoam);
        var screen = Screen.FromControl(form).WorkingArea;
        roam.Relocate(new PointF(screen.Right - form.OrangeHeight / 2, screen.Top + form.OrangeHeight / 2), false);
        Invoke("UpdateOutsideCharacter", new object?[] { null }!);
        var edgeBody = BodyPoint();
        var edgePosition = roam.Position;
        Invoke("UpdateCharacterHover", edgeBody);
        Advance(1.333, edgeBody);
        var edgeSprite = CharacterVisual.SpriteBounds(roam.Position, form.OrangeHeight, 0, CharacterEffect.Startled, 1);
        var alert = CharacterVisual.AlertBounds(edgeSprite, form.OrangeHeight, SystemInformation.VirtualScreen);
        check(roam.Position == edgePosition && SystemInformation.VirtualScreen.Contains(Rectangle.Ceiling(alert)),
            "화면 모서리 hover도 몸을 밀지 않고 느낌표를 화면 안에 표시");
        form.SetCharacterDisplay(CharacterDisplay.Outside);
        Invoke("UpdateCharacterHover", BodyPoint());
        form.SetCharacterDisplay(CharacterDisplay.Inside);
        check(!Field<bool>("characterHovered"), "표시 모드 변경은 이전 hover 정지를 해제");
        Invoke("UpdateCharacterHover", BodyPoint());
        form.Hide();
        check(!Field<bool>("characterHovered"), "창 숨김은 hover 정지를 해제");
        form.Close();
    }
}
