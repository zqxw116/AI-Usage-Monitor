using UsageMonitor;

internal static class CharacterRoamingChecks
{
    internal static void Run(Action<bool, string> check)
    {
        var area = new Rectangle(-1200, -300, 1000, 800);
        var start = new PointF(-700, 100);
        var roaming = new CharacterRoaming(new Random(42));
        roaming.Reset(start);
        check(roaming.Step(0, area, 48) == start && roaming.Step(-1, area, 48) == start,
            "이동 시간이 0 이하이면 좌표를 유지");

        var twin = new CharacterRoaming(new Random(42));
        twin.Reset(start);
        bool bounded = true, deterministic = true, slow = true, facing = true;
        double distance = 0;
        for (int i = 0; i < 12000; i++)
        {
            var before = roaming.Position;
            var after = roaming.Step(1d / 60, area, 48);
            deterministic &= after == twin.Step(1d / 60, area, 48);
            bounded &= after.X >= -1176 && after.X <= -224 && after.Y >= -276 && after.Y <= 476;
            double step = Distance(before, after);
            if (after.X != before.X) facing &= roaming.FacingLeft == (after.X < before.X);
            slow &= step <= 24d / 60 + .001;
            distance += step;
        }
        check(deterministic, "같은 난수 시드로 자유 이동 경로를 재현");
        check(bounded, "장시간 자유 이동 중 음수 모니터 좌표와 이미지 여백 유지");
        check(slow && distance > 1500, "자유 이동은 초당 24픽셀 이하로 계속 진행");
        check(facing, "자유 이동은 목표 대신 실제 수평 이동 방향을 바라봄");

        roaming.Reset(start);
        var pointer = new PointF(-400, 200);
        var first = roaming.Step(.016, area, 48, pointer);
        check(first.X > start.X && first.Y > start.Y && Distance(start, first) <= 24 * .016 + .001 && !roaming.FacingLeft,
            "마우스 급이동에도 자유 이동과 같은 저속으로 이동하며 오른쪽을 바라봄");
        bool followSlow = true;
        for (int i = 0; i < 3000; i++)
        {
            var before = roaming.Position;
            var after = roaming.Step(1d / 60, area, 48, pointer);
            followSlow &= Distance(before, after) <= 24d / 60 + .001;
        }
        check(followSlow, "마우스에 가까워질 때까지 모든 프레임에서 초당 24픽셀 이하 유지");
        check(Distance(roaming.Position, new PointF(-364, 228)) < .02,
            "마우스 위치에서 오른쪽 아래로 간격을 두고 정착");

        var longFrameRoaming = new CharacterRoaming(new Random(19));
        var cappedFrameRoaming = new CharacterRoaming(new Random(19));
        longFrameRoaming.Reset(start);
        cappedFrameRoaming.Reset(start);
        check(longFrameRoaming.Step(20, area, 48, pointer) == cappedFrameRoaming.Step(.1, area, 48, pointer),
            "긴 프레임은 0.1초만 이동");

        for (int i = 0; i < 3000; i++) roaming.Step(1d / 60, area, 48, new PointF(9000, -9000));
        check(Distance(roaming.Position, new PointF(-224, -276)) < .02,
            "영역 밖 커서를 따라갈 때도 이미지 반 크기 여백을 유지");
        roaming.Reset(start);
        check(roaming.Step(.1, new Rectangle(-50, 20, 60, 40), 100) == new PointF(-20, 40),
            "캐릭터가 작업 영역보다 크면 각 축 중앙에 배치");

        roaming.Reset(start);
        var resetFirst = roaming.Step(.016, area, 48, pointer);
        check(Distance(start, resetFirst) < .02, "Reset은 이전 이동 속도를 제거하여 복귀 후 튀지 않음");

        var coarse = new CharacterRoaming(new Random(11));
        var fine = new CharacterRoaming(new Random(11));
        coarse.Reset(start);
        fine.Reset(coarse.Position);
        for (int i = 0; i < 10; i++) coarse.Step(.1, area, 48, pointer);
        for (int i = 0; i < 100; i++) fine.Step(.01, area, 48, pointer);
        check(Distance(coarse.Position, fine.Position) < 1, "마우스 추종 가속이 프레임 간격 변화에도 1픽셀 이내로 일치");
        roaming.Reset(start);
        var leftward = roaming.Step(.1, area, 48, new PointF(-1000, 72));
        check(leftward.X < start.X && roaming.FacingLeft, "마우스가 왼쪽에 있으면 실제 이동에 맞춰 왼쪽을 바라봄");
        bool leftFacing = roaming.FacingLeft;
        roaming.Step(0, area, 48, pointer);
        check(roaming.FacingLeft == leftFacing, "정지하면 마지막 이동 방향을 유지");

        var free = new CharacterRoaming(new Random(73));
        var follow = new CharacterRoaming(new Random(73));
        free.Reset(start);
        follow.Reset(start);
        var freeFirst = free.Step(1d / 60, area, 48);
        var destination = (PointF)typeof(CharacterRoaming).GetField("target", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(free)!;
        var matchedPointer = new PointF(destination.X - 36, destination.Y - 28);
        bool matched = freeFirst == follow.Step(1d / 60, area, 48, matchedPointer);
        bool slowed = false;
        for (int i = 0; i < 3000 && Distance(free.Position, destination) >= 12.5; i++)
        {
            var before = free.Position;
            var after = free.Step(1d / 60, area, 48);
            var followed = follow.Step(1d / 60, area, 48, matchedPointer);
            matched &= Distance(after, followed) < .001 && free.FacingLeft == follow.FacingLeft;
            slowed |= Distance(after, destination) < 30 && Distance(before, after) < 18d / 60;
        }
        check(matched && slowed, "두 모드는 같은 목적지에서 출발 가속·순항·근거리 감속·바라보는 방향이 일치");
        var desktop = new Rectangle(0, 0, 3840, 1400);
        var shortScreen = new Rectangle(0, 0, 1920, 1040);
        roaming.Reset(new PointF(1000, 1000));
        for (int i = 0; i < 600; i++) roaming.Step(1d / 60, desktop, 48, new PointF(1000, 1020), shortScreen);
        check(roaming.Position.Y <= 1016.02f, "높이가 다른 모니터에서도 추종 목표가 작업표시줄을 침범하지 않음");
        var previous = roaming.Position;
        var crossed = roaming.Step(.016, desktop, 48, new PointF(3000, 1300), new Rectangle(1920, 0, 1920, 1400));
        check(Distance(previous, crossed) <= 24 * .016 + .001, "커서가 다른 모니터로 건너가도 자유 이동과 같은 저속 유지");
    }

    private static double Distance(PointF a, PointF b) =>
        Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));
}
