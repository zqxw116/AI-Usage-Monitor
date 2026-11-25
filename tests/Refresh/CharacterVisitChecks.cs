using UsageMonitor;

internal static class CharacterVisitChecks
{
    internal static void RenderPreview(string directory)
    {
        var area = new Rectangle(0, 0, 360, 240);
        using var strip = new Bitmap(area.Width * OrangeWalker.Characters.Length, area.Height);
        using var graphics = Graphics.FromImage(strip);
        graphics.Clear(Color.FromArgb(78, 88, 100));
        for (int i = 0; i < OrangeWalker.Characters.Length; i++)
        {
            using var walker = new OrangeWalker(OrangeWalker.Characters[i]);
            var pose = CharacterVisit.PoseAt(1.1, new PointF(30, 30), 22, 0, area, walker.Face);
            var saved = graphics.Save();
            graphics.TranslateTransform(i * area.Width, 0);
            graphics.SetClip(area);
            walker.DrawSprite(graphics, pose.Center, pose.Angle, 0, pose.Size);
            graphics.Restore(saved);
        }
        strip.Save(Path.Combine(directory, "캐릭터 얼굴 확대 미리보기.png"));
    }

    internal static void Run(Action<bool, string> check)
    {
        var origin = new PointF(340, 180);
        var outside = new PointF(340, 140);
        var face = new RectangleF(.2f, .1f, .5f, .4f);
        const float originalSize = 24, originalAngle = 270;
        var original = new CharacterVisitPose(origin, originalSize, originalAngle);
        foreach (var area in new[] { new Rectangle(0, 0, 1920, 1080), new Rectangle(-2560, -320, 2560, 1440),
            new Rectangle(1920, 240, 1280, 1024) })
        {
            CharacterVisitPose At(double elapsed) => CharacterVisit.PoseAt(elapsed, origin,
                originalSize, originalAngle, area, face);
            bool Near(float left, float right) => Math.Abs(left - right) < .02f;
            check(At(0) == original && At(CharacterVisit.Duration) == original, "방문 시작·종료에 원래 위치·크기·각도 복귀 " + area.Location);
            check(At(-1) == original && At(4) == original, "방문 시간 범위 밖은 원래 모습 " + area.Location);
            check(At(.03).Size > originalSize && At(.03).Center != outside, "클릭한 위치에서 외부 경유 없이 바로 확대 " + area.Location);
            var close = At(.85);
            check(close == At(1.1) && close == At(1.5), "0.85초 확대 후 0.65초간 얼굴 유지 " + area.Location);
            check(CharacterVisit.Duration == 2.05 && At(1.6).Size < close.Size && At(2).Size > originalSize && At(2.05) == original,
                "마지막 0.55초에 축소하고 2.05초에 원래 모습 복귀 " + area.Location);
            double returnedEarly = (close.Size - At(1.61).Size) / (close.Size - originalSize);
            double returnedMiddle = (close.Size - At(1.775).Size) / (close.Size - originalSize);
            double returnedLate = (close.Size - At(1.94).Size) / (close.Size - originalSize);
            check(returnedEarly < .01 && Math.Abs(returnedMiddle - .125) < .001 && returnedLate > .51,
                "복귀는 천천히 시작해 마지막으로 갈수록 가속 " + area.Location);
            var target = CharacterVisit.TargetBounds(area);
            float expectedSize = Math.Min(area.Width * (702f / 1911), area.Height * (619f / 1083));
            check(Near(close.Size, expectedSize) && close.Angle == 0, "지정 박스 안에 원본 전체가 들어가는 확대 크기 " + area.Location);
            check(At(.1).Size - originalSize > 10 && At(.4).Size - At(.3).Size > At(.1).Size - originalSize,
                "초반부터 눈에 보이게 커지고 중간 확대 증가량은 더 큼 " + area.Location);
            var full = new RectangleF(close.Center.X - close.Size / 2, close.Center.Y - close.Size / 2, close.Size, close.Size);
            check(full.Left >= target.Left - .02f && full.Right <= target.Right + .02f && full.Top >= target.Top - .02f &&
                full.Bottom <= target.Bottom + .02f && Near(close.Center.X, target.Left + target.Width / 2),
                "음수 모니터 원점도 반영해 지정 박스 비율·위치에 전체 캐릭터 배치 " + area.Location);
            bool finite = true, sizeRange = true, expanding = true, shrinking = true;
            var previous = At(0);
            for (int i = 0; i <= 600; i++)
            {
                var pose = At(i / 200d);
                finite &= float.IsFinite(pose.Center.X) && float.IsFinite(pose.Center.Y) && float.IsFinite(pose.Size) && float.IsFinite(pose.Angle);
                sizeRange &= pose.Size >= originalSize && pose.Size <= close.Size;
                if (i / 200d <= .85) expanding &= pose.Size >= previous.Size;
                if (i / 200d >= 1.5) shrinking &= pose.Size <= previous.Size;
                previous = pose;
            }
            check(expanding && shrinking, "확대와 축소 중 크기 반전이나 튀는 움직임 없음 " + area.Location);
            check(finite && sizeRange, "연출 전체에서 유한 좌표와 확대 크기 범위 유지 " + area.Location);
            bool continuous = true, stoppedAtBoundaries = true;
            foreach (double boundary in new[] { 0d, .85, 1.5, 2.05 })
            {
                var left = At(boundary - .0000001);
                var right = At(boundary + .0000001);
                continuous &= Near(left.Center.X, right.Center.X) && Near(left.Center.Y, right.Center.Y) &&
                    Near(left.Size, right.Size) && Near(left.Angle, right.Angle);
                var at = At(boundary);
                if (boundary < CharacterVisit.Duration)
                    stoppedAtBoundaries &= Math.Abs(left.Center.X - at.Center.X) < .001f && Math.Abs(right.Center.X - at.Center.X) < .001f &&
                        Math.Abs(left.Size - at.Size) < .001f && Math.Abs(right.Size - at.Size) < .001f;
            }
            check(continuous && stoppedAtBoundaries, "위치·크기·각도 경계 연속과 정지 구간의 부드러운 연결 " + area.Location);
        }
        var smallArea = new Rectangle(0, 0, 100, 80);
        var large = CharacterVisit.PoseAt(1.1, origin, 500, originalAngle, smallArea, face);
        check(large.Size < 500 && large.Size <= CharacterVisit.TargetBounds(smallArea).Width,
            "원래 크기가 지정 박스보다 커도 확대 정지 시 박스 안에 맞춤");
        var centerFace = new RectangleF(.25f, .25f, .5f, .5f);
        var centered = CharacterVisit.PoseAt(1.1, origin, originalSize, originalAngle, smallArea, centerFace);
        var smallTarget = CharacterVisit.TargetBounds(smallArea);
        check(centered.Center == new PointF(smallTarget.Left + smallTarget.Width / 2, smallTarget.Top + smallTarget.Height / 2),
            "얼굴 부분 좌표 대신 원본 전체 중심으로 배치");
        var reference = CharacterVisit.PoseAt(1.1, origin, 32, 0, new Rectangle(0, 0, 1911, 1083), face);
        check(Math.Abs(reference.Size - 619) < .02 && Math.Abs(reference.Center.X - 1039) < .02 &&
            Math.Abs(reference.Center.Y - 773.5) < .02, "1911×1083 기준 사용자 지정 x688~1390 y464~1083 박스 반영");
        using var actual = new OrangeWalker(OrangeWalker.Characters[0], 32);
        var previewArea = new Rectangle(0, 0, 800, 450);
        float PixelsAt(double seconds) => CharacterVisit.PoseAt(seconds, origin, 32, 0, previewArea, actual.Face).Size;
        check(PixelsAt(1d / 30) >= 33 && PixelsAt(.1) >= 40, "800×450 전체 원본 기준 초반부터 확대가 보임");
        Console.WriteLine("실제 미리보기 크기(px): " + string.Join(", ", new[] { 0d, 1d / 30, .1, .2, .4, .85, 1.5, 1.61, 1.775, 1.94, 2.05 }
            .Select(seconds => $"{seconds:0.000}s={PixelsAt(seconds):0.0}")));
    }
}
