using System.Reflection;
using UsageMonitor;

internal static class CharacterDragChecks
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    internal static void Run(string directory, Action<bool, string> check)
    {
        using var walker = new OrangeWalker(size: 24);
        var design = new Size(1030, 50);
        var rectangle = new Rectangle(100, 100, design.Width, design.Height);
        var inside = CharacterAttachment.Find(walker, design, rectangle, 1, new PointF(600, 148));
        var outside = CharacterAttachment.Find(walker, design, rectangle, 1, new PointF(600, 96));
        check(inside is { Mode: CharacterDisplay.Inside } && outside is { Mode: CharacterDisplay.Outside },
            "발끝이 박스 안/밖 어느 쪽인지에 따라 내부/외부 스냅 선택");
        check(CharacterAttachment.Find(walker, design, rectangle, 1, new PointF(600, 400)) is null,
            "발끝이 테두리에서 멀면 스냅하지 않음");
        bool edges = true;
        foreach (var foot in new[] { new PointF(600, 98), new PointF(1132, 125), new PointF(600, 152), new PointF(98, 125) })
        {
            var snap = CharacterAttachment.Find(walker, design, rectangle, 1, foot);
            edges &= snap is { } s && Math.Abs(s.Separation - 2) < .1;
        }
        check(edges, "외부 네 변 발끝 스냅과 사각형 경로 방향 일치");

        var settings = new MonitorSettings { VisualEffects = false };
        using var codex = new UsageSource(false);
        using var claude = new UsageSource(true);
        using var form = new MonitorForm(codex, claude, false, settings);
        form.Show();
        var area = Screen.PrimaryScreen!.WorkingArea;
        form.Location = new Point(area.Left + 60, area.Top + 120);
        Application.DoEvents();
        void Invoke(string name, params object[] args) => typeof(MonitorForm).GetMethod(name, Private)!.Invoke(form, args);
        T Field<T>(string name) => (T)typeof(MonitorForm).GetField(name, Private)!.GetValue(form)!;
        Point TrackCenter(bool outer)
        {
            var current = Field<OrangeWalker>("orange");
            double distance = Field<double>("orangeDistance");
            var pose = current.PoseAt(distance / current.Perimeter(form.DesignSize) * current.Perimeter(form.DesignSize, outer), form.DesignSize, outer);
            float scale = form.ClientSize.Width / (float)form.DesignSize.Width;
            var origin = form.PointToScreen(Point.Empty);
            return Point.Round(new PointF(origin.X + pose.Center.X * scale, origin.Y + pose.Center.Y * scale));
        }
        form.PreviewOrange(.3, 0);
        var initial = TrackCenter(false);
        Invoke("BeginCharacterPress", initial, false);
        Invoke("MoveCharacterPress", new Point(initial.X + 1, initial.Y));
        check(!Field<bool>("draggingCharacter") && !form.CharacterVisitActive, "드래그 문턱 전에는 이동·확대 실행을 보류");
        Invoke("EndCharacterPress", initial);
        check(form.CharacterVisitActive, "내부 짧은 클릭은 기존 확대 연출 실행");
        form.SetCharacterDisplay(CharacterDisplay.Inside);
        initial = TrackCenter(false);
        Invoke("BeginCharacterPress", initial, false);
        var grabOrigin = Field<PointF>("dragOrigin");
        var far = new Point(form.Left + form.Width / 2, form.Bottom + 180);
        var expectedDrop = new PointF(far.X - (initial.X - grabOrigin.X), far.Y - (initial.Y - grabOrigin.Y));
        Invoke("MoveCharacterPress", far);
        check(Field<bool>("draggingCharacter") && Field<PointF>("dragCenter") == expectedDrop && !form.CharacterVisitActive,
            "문턱을 넘으면 확대 없이 캐릭터만 포인터 위치로 즉시 이동");
        Invoke("EndCharacterPress", far);
        check(settings.CharacterDisplay == CharacterDisplay.FollowMouse && settings.CharacterDesktopX == expectedDrop.X && settings.CharacterDesktopY == expectedDrop.Y,
            "테두리에서 먼 드롭은 추적 모드와 드롭 좌표 저장");
        var overlay = form.OwnedForms.OfType<OutsideCharacterForm>().Single();
        var roam = Field<CharacterRoaming>("roaming");
        Invoke("BeginCharacterPress", Point.Round(roam.Position), true);
        Invoke("EndCharacterPress", Point.Round(roam.Position));
        check(form.CharacterVisitActive, "추적 모드 짧은 클릭도 확대 실행");
        form.PreviewOrange(.3, 3);
        Invoke("BeginCharacterPress", Point.Round(roam.Position), true);
        var target = new Point(form.Left + form.Width / 2, form.Top - (int)form.OrangeHeight / 2 - 4);
        Invoke("MoveCharacterPress", target);
        check(Field<CharacterSnap?>("dragSnap") is { Mode: CharacterDisplay.Outside }, "드래그 중 발끝 외부 스냅 프리뷰 계산");
        Invoke("EndCharacterPress", target);
        check(settings.CharacterDisplay == CharacterDisplay.Outside && overlay.Visible && !form.CharacterVisitActive,
            "외부 드롭 직후 사각형 걷기로 복귀");
        var expected = TrackCenter(true);
        var hitCenter = (PointF)typeof(OutsideCharacterForm).GetField("hitCenter", Private)!.GetValue(overlay)!;
        check(Math.Abs(hitCenter.X - expected.X) <= 1 && Math.Abs(hitCenter.Y - expected.Y) <= 1,
            "스냅한 경로 위치와 외부 렌더·히트 중심 동기화");
        string path = Path.Combine(directory, "drag-placement.json");
        settings.Save(path);
        var restored = MonitorSettings.Load(path);
        check(restored.CharacterDisplay == settings.CharacterDisplay && restored.CharacterTrackDistance == settings.CharacterTrackDistance,
            "드롭 모드와 사각형 경로 위치 재실행용 저장");
        initial = TrackCenter(true);
        form.PreviewOrange(.79, 2);
        initial = TrackCenter(true);
        Invoke("BeginCharacterPress", initial, true);
        var actualOrigin = Field<PointF>("dragOrigin");
        var shownCenter = (PointF)typeof(OutsideCharacterForm).GetField("hitCenter", Private)!.GetValue(overlay)!;
        check(Math.Abs(actualOrigin.X - shownCenter.X) < .01 && Math.Abs(actualOrigin.Y - shownCenter.Y) < .01,
            "외부 경로 먼 모서리에서도 잡기 시작 중심이 표시된 위치와 일치");
        Invoke("MoveCharacterPress", new Point(initial.X + 150, initial.Y + 150));
        var edge = new Point(area.Left + 1, area.Top + 1);
        Invoke("MoveCharacterPress", edge);
        var effectBounds = CharacterVisual.Bounds(Field<PointF>("dragCenter"), form.OrangeHeight, 0, CharacterEffect.None);
        check(effectBounds.Left >= area.Left - .01 && effectBounds.Top >= area.Top - .01 &&
            effectBounds.Right <= area.Right + .01 && effectBounds.Bottom <= area.Bottom + .01,
            "화면 가장자리에서 드래그 몸 전체를 작업 영역 안에 유지");
        Invoke("CancelCharacterPress");
        check(!Field<bool>("draggingCharacter") && settings.CharacterDisplay == CharacterDisplay.Outside && TrackCenter(true) == initial,
            "입력 취소는 모드와 원래 사각형 위치 복원");
        form.Close();
    }
}
