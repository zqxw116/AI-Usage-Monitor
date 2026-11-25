using UsageMonitor;

internal static class CharacterRoamingChecks
{
    internal static void Run(Action<bool, string> check)
    {
        var area = new Rectangle(-2000, -1000, 6000, 3000);
        var start = new PointF(0, 0);
        var free = new CharacterRoaming(new Random(42));
        var twin = new CharacterRoaming(new Random(42));
        free.Reset(start); twin.Reset(start);
        bool same = true, slow = true, facing = true, bounded = true;
        double traveled = 0;
        for (int i = 0; i < 12000; i++)
        {
            var before = free.Position;
            var after = free.Step(1d / 60, area, 48);
            same &= after == twin.Step(1d / 60, area, 48);
            double moved = Distance(before, after);
            slow &= moved <= 24d / 60 + .001;
            traveled += moved;
            if (before.X != after.X) facing &= free.FacingLeft == (after.X < before.X);
            bounded &= after.X >= area.Left + 24 && after.X <= area.Right - 24 && after.Y >= area.Top + 24 && after.Y <= area.Bottom - 24;
        }
        check(same && slow && traveled > 1500, "자유 이동은 기존 18~24픽셀 저속 경로와 결정성 유지");
        check(facing && bounded, "자유 이동 실제 진행 방향과 화면 여백 유지");
        var follow = new CharacterRoaming(new Random(73));
        follow.Reset(start);
        var cursor = new PointF(800, 0);
        var recognized = follow.Step(1d / 60, area, 48, cursor);
        check(recognized != start && follow.AnimationSeconds > 0 && follow.Startled && follow.ReactionAge == 0,
            "추적 인지 느낌표는 이동과 애니메이션을 중단하지 않음");
        bool proportional = true, actualFacing = true, capped = true;
        double baseSpeed = (float)typeof(CharacterRoaming).GetField("speed", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(follow)!;
        double maximum = 0;
        for (int i = 0; i < 3000; i++)
        {
            var before = follow.Position;
            double animation = follow.AnimationSeconds;
            var after = follow.Step(1d / 60, area, 48, cursor);
            double moved = Distance(before, after);
            maximum = Math.Max(maximum, moved * 60);
            proportional &= Math.Abs((follow.AnimationSeconds - animation) * baseSpeed - moved) < .001;
            if (before.X != after.X) actualFacing &= follow.FacingLeft == (after.X < before.X);
            capped &= moved <= 720d / 60 + .001;
        }
        check(proportional && actualFacing && capped && maximum > 6 * 18, "실제 이동량 비례 프레임·진행 방향·원거리 가속 및 720픽셀 상한");
        check(follow.Nearby, "몸 가장자리 12픽셀 안에 접근하면 도착 후 배회 상태 유지");
        var nearby = follow.Position;
        var farCursor = new PointF(nearby.X - 500, nearby.Y);
        double priorAnimation = follow.AnimationSeconds;
        check(follow.Step(.1, area, 48, farCursor) != nearby && follow.Startled && follow.AnimationSeconds > priorAnimation,
            "도착 후 먼 커서 이동에는 느낌표와 함께 다시 추적");
        for (int i = 0; i < 90; i++) follow.Step(1d / 60, area, 48, farCursor);
        check(!follow.Nearby && follow.FacingLeft, "커서가 반대편으로 이동하면 다시 왼쪽 추적");

        double SampleSpeed(float distance)
        {
            var subject = new CharacterRoaming(new Random(5));
            subject.Reset(start);
            var pointer = new PointF(distance, 0);
            double peak = 0;
            for (int i = 0; i < 60; i++)
            {
                var before = subject.Position;
                var after = subject.Step(1d / 60, area, 48, pointer);
                peak = Math.Max(peak, Distance(before, after) * 60);
            }
            return peak;
        }
        double far = SampleSpeed(1600), rush = SampleSpeed(600), decelerating = SampleSpeed(250), creeping = SampleSpeed(70);
        check(far > rush && rush > decelerating && decelerating > creeping && creeping < 24,
            "몸 가장자리 360밖은 멀수록 빠르고 100~360 감속·12~100 정상 걸음");
        var waiting = new CharacterRoaming(new Random(5));
        waiting.Reset(start);
        var nearPointer = new PointF(30, 0);
        for (int i = 0; i < 30; i++) waiting.Step(1d / 60, area, 48, nearPointer);
        check(waiting.Position != start && waiting.AnimationSeconds > 0 && waiting.Nearby && !waiting.Startled,
            "근처 커서에서는 명확한 정지 후 배회 시작");
        for (int i = 0; i < 600; i++) waiting.Step(1d / 60, area, 48, new PointF(30 + (i % 2 == 0 ? .5f : -.5f), 0));
        check(waiting.Nearby && !waiting.Startled, "미세한 커서 떨림은 추적 재개 없이 전체 영역 배회 유지");

        var normal = new CharacterRoaming(new Random(5));
        normal.Reset(start);
        var normalCursor = new PointF(110, 0);
        bool fixedWalk = true;
        var stepDurations = new List<double>();
        for (int i = 0; i < 600 && !normal.Nearby; i++)
        {
            var before = normal.Position;
            long started = System.Diagnostics.Stopwatch.GetTimestamp();
            normal.Step(1d / 60, area, 48, normalCursor);
            stepDurations.Add(System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            if (!normal.Nearby) fixedWalk &= Distance(before, normal.Position) * 60 is >= 17.99 and <= 24.01;
        }
        check(fixedWalk && normal.Nearby && normal.ArrivalPaused && normal.ArrivalProgress == 0 && normal.Position.X < normalCursor.X - 24,
            "몸 경계 12픽셀에서 중심 겹침 전에 도착하고 명확히 정지하며 즉시 음표 시작");
        var arrivalPosition = normal.Position;
        double arrivalAnimation = normal.AnimationSeconds;
        bool held = true;
        for (int i = 0; i < 26; i++)
        {
            normal.Step(1d / 60, area, 48, normalCursor);
            held &= normal.Position == arrivalPosition && normal.AnimationSeconds == arrivalAnimation && double.IsFinite(normal.ArrivalProgress);
        }
        check(held && normal.ArrivalPaused, "도착 후 0.433초 동안 위치·걷기 프레임 정지하며 음표 표시 유지");
        for (int i = 0; i < 2; i++) normal.Step(1d / 60, area, 48, normalCursor);
        check(!normal.ArrivalPaused && double.IsFinite(normal.ArrivalProgress) && normal.Position != arrivalPosition,
            "0.45초 정지 뒤 음표를 마무리하며 일반 자유 이동 시작");
        for (int i = 0; i < 90; i++) normal.Step(1d / 60, area, 48, normalCursor);
        check(normal.Nearby && !double.IsFinite(normal.ArrivalProgress), "커서가 정지해 있으면 음표를 반복하지 않음");
        Console.WriteLine($"도착 접근 Step 처리(ms): 평균={stepDurations.Average():0.0000}, 최대={stepDurations.Max():0.0000}");
        var repeatPointer = new PointF(normal.Position.X + 300, normal.Position.Y);
        normal.Step(1d / 60, area, 48, repeatPointer);
        check(!normal.Nearby && !double.IsFinite(normal.ArrivalProgress) && normal.Startled, "도착 당시 커서가 3픽셀 넘게 움직이면 다시 추적");
        for (int i = 0; i < 1800 && !normal.Nearby; i++) normal.Step(1d / 60, area, 48, repeatPointer);
        check(normal.Nearby && normal.ArrivalPaused, "재추적 후 다시 도착하면 정지·음표·배회 주기를 반복");
        var dropped = new CharacterRoaming(new Random(5));
        dropped.Reset(start);
        dropped.RequireFollowApproach();
        for (int i = 0; i < 60; i++) dropped.Step(1d / 60, area, 48, new PointF(20, 0));
        check(!dropped.Nearby, "드롭 직후 몸 근처 커서만으로 도착 처리하지 않음");
        var departedCursor = new PointF(dropped.Position.X + 220, dropped.Position.Y);
        for (int i = 0; i < 1200 && !dropped.Nearby; i++) dropped.Step(1d / 60, area, 48, departedCursor);
        check(dropped.Nearby, "드롭 후 커서가 멀어졌다가 실제 접근하면 도착 처리");
        var firstDrop = new CharacterRoaming(new Random(5));
        firstDrop.Relocate(start, false);
        firstDrop.RequireFollowApproach();
        firstDrop.Step(1d / 60, area, 48, new PointF(300, 0));
        check(firstDrop.Position.X > 0 && double.IsFinite(firstDrop.AnimationSeconds) && firstDrop.AnimationSeconds > 0,
            "내부에서 처음 떼어내도 기본 속도가 초기화되어 이동·걷기 프레임이 유효");
        var large = new CharacterRoaming(new Random(5));
        large.Reset(start);
        large.Step(1d / 60, area, 400, new PointF(210, 0));
        check(large.Nearby && !large.Startled, "큰 캐릭터는 중심이 아닌 실제 몸 가장자리 거리로 도착 판단");
        var padded = new CharacterRoaming(new Random(5));
        padded.Reset(start);
        padded.Step(1d / 60, area, 400, new PointF(220, 0), bodyBounds: new RectangleF(.4f, .1f, .2f, .9f));
        check(!padded.Nearby && padded.Startled, "투명 여백은 몸 거리에서 제외하여 성긴 이미지의 오도착 방지");
        var mirrored = new CharacterRoaming(new Random(5));
        mirrored.Reset(start);
        mirrored.Relocate(start, true);
        mirrored.Step(1d / 60, area, 200, new PointF(-110, 0), bodyBounds: new RectangleF(.7f, .1f, .3f, .9f));
        check(mirrored.Nearby, "왼쪽 반전된 비대칭 몸의 가장자리로 거리 계산");
        foreach (float size in new[] { 32f, 96f, 400f })
        {
            var sized = new CharacterRoaming(new Random(5));
            sized.Reset(new PointF(-1800, 0));
            var actualCursor = new PointF(-1400, 0);
            var body = new RectangleF(.25f, .1f, .4f, .8f);
            for (int i = 0; i < 3000 && !sized.Nearby; i++) sized.Step(1d / 60, area, size, actualCursor, bodyBounds: body);
            double edgeGap = actualCursor.X - (sized.Position.X + (body.Right - .5f) * size);
            check(sized.ArrivalPaused && edgeGap > 0 && edgeGap <= 12 && sized.Position.X < actualCursor.X,
                $"{size}px 캐릭터는 음수 화면에서도 투명 여백을 제외한 몸 경계 12px 안에서 중심 겹침 전에 정지");
        }
        var beyondScreen = new CharacterRoaming(new Random(5));
        beyondScreen.Reset(new PointF(1000, 1000));
        for (int i = 0; i < 600; i++) beyondScreen.Step(1d / 60, new Rectangle(0, 0, 1920, 1040), 48,
            new PointF(1000, 1200), new Rectangle(0, 0, 1920, 1040));
        check(!beyondScreen.Nearby, "작업 영역에 보정한 목표가 아닌 실제 커서와 몸 경계 거리로만 도착 판단");
        var turning = new CharacterRoaming(new Random(5));
        turning.Reset(start);
        var movingCursor = new PointF(800, 0);
        for (int i = 0; i < 40; i++) turning.Step(1d / 60, area, 48, movingCursor);
        bool noJitterSignal = true;
        for (int i = 0; i < 60; i++)
        {
            turning.Step(1d / 60, area, 48, new PointF(800 + (i % 2 == 0 ? .8f : -.8f), 0));
            noJitterSignal &= !turning.Startled;
        }
        var prior = turning.Position;
        turning.Step(1d / 60, area, 48, new PointF(760, 0));
        check(noJitterSignal && turning.Startled && turning.Position != prior,
            "커서 잔떨림은 무시하고 유의미한 이동 방향 반전에 이동 중 느낌표 표시");
        var interrupted = new CharacterRoaming(new Random(5));
        interrupted.Reset(start);
        interrupted.Step(1d / 60, area, 48, new PointF(30, 0));
        var pausedPosition = interrupted.Position;
        interrupted.Step(1d / 60, area, 48, new PointF(32.9f, 0));
        check(interrupted.ArrivalPaused && interrupted.Position == pausedPosition,
            "도착 당시 커서에서 3픽셀 이내 입력 잡음은 정지·음표 유지");
        interrupted.Step(1d / 60, area, 48, new PointF(33.1f, 0));
        check(!interrupted.Nearby && !interrupted.ArrivalPaused && !double.IsFinite(interrupted.ArrivalProgress) && interrupted.Position != pausedPosition,
            "정지 중 가까운 커서도 3픽셀 넘게 움직이면 즉시 음표·정지를 취소하고 추적");
        interrupted.Step(1d / 60, area, 48, new PointF(33.1f, 0));
        check(!interrupted.Nearby && interrupted.Position != pausedPosition,
            "가까운 커서 추적 재개 뒤 같은 위치에서 도착을 즉시 재발동하지 않음");
        for (int i = 0; i < 60 && !interrupted.Nearby; i++) interrupted.Step(1d / 60, area, 48, new PointF(33.1f, 0));
        check(interrupted.ArrivalPaused && Distance(pausedPosition, interrupted.Position) >= 2,
            "커서 근처 이동에도 실제 접근 후 새 위치에서 도착을 다시 허용");
        var global = new CharacterRoaming(new Random(5));
        var globalTwin = new CharacterRoaming(new Random(5));
        global.Reset(start);
        var fixedPointer = new PointF(30, 0);
        global.Step(1d / 60, area, 48, fixedPointer);
        for (int i = 0; i < 27; i++) global.Step(1d / 60, area, 48, fixedPointer);
        globalTwin.Reset(start);
        bool equalFreeMovement = true, noSelfRetrack = true;
        double maximumFromPointer = 0;
        for (int i = 0; i < 12000; i++)
        {
            var position = global.Step(1d / 60, area, 48, fixedPointer);
            equalFreeMovement &= position == globalTwin.Step(1d / 60, area, 48);
            noSelfRetrack &= global.Nearby;
            maximumFromPointer = Math.Max(maximumFromPointer, Distance(position, fixedPointer));
        }
        check(equalFreeMovement && noSelfRetrack && maximumFromPointer > 500,
            "정지 커서에서는 자유 이동과 같은 전역 무작위 경로로 500픽셀 넘게 배회해도 자체 재추적하지 않음");
        var screen = new Rectangle(0, 0, 1920, 1040);
        follow.Reset(new PointF(1000, 1000));
        for (int i = 0; i < 1200; i++) follow.Step(1d / 60, screen, 48, new PointF(1000, 1020), screen);
        check(follow.Position.Y <= 1016.01, "현재 모니터의 전역 배회가 작업표시줄 침범하지 않음");
        var saved = follow.Position;
        bool savedFacing = follow.FacingLeft;
        follow.Relocate(new PointF(300, 400), savedFacing);
        check(follow.Position == new PointF(300, 400) && follow.FacingLeft == savedFacing, "드롭 좌표와 방향 복원 시 관성 제거");
        check(follow.Step(0, area, 48, cursor) == follow.Position && follow.Step(-1, area, 48, cursor) == follow.Position,
            "0 이하 시간에는 좌표 유지");
    }

    private static double Distance(PointF a, PointF b) => Math.Sqrt(Math.Pow(a.X - b.X, 2) + Math.Pow(a.Y - b.Y, 2));
}
