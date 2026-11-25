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
            float expectedSize = Math.Min(area.Width * .45f / face.Width, area.Height * .45f / face.Height);
            check(Near(close.Size, expectedSize) && close.Angle == 0, "얼굴 영역이 화면의 45%에 맞는 확대 크기 " + area.Location);
            check(At(.1).Size - originalSize > 10 && At(.4).Size - At(.3).Size > At(.1).Size - originalSize,
                "초반부터 눈에 보이게 커지고 중간 확대 증가량은 더 큼 " + area.Location);
            var faceCenter = new PointF(close.Center.X + (face.Left + face.Width / 2 - .5f) * close.Size,
                close.Center.Y + (face.Top + face.Height / 2 - .5f) * close.Size);
            check(Near(faceCenter.X, area.Left + area.Width / 2f) && Near(faceCenter.Y, area.Top + area.Height / 2f),
                "음수 모니터 원점도 반영해 얼굴을 화면 중앙에 배치 " + area.Location);
            var mirrored = CharacterVisit.PoseAt(1.1, origin, originalSize, originalAngle, area, face, mirrorX: true);
            check(Near(mirrored.Size, close.Size) && Near(mirrored.Center.X + (.5f - face.Left - face.Width / 2) * mirrored.Size,
                area.Left + area.Width / 2f), "왼쪽을 보는 반전 캐릭터도 얼굴이 화면 중앙에 맞음 " + area.Location);
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
        check(large.Size == 500, "이미 큰 캐릭터는 방문 시 축소하지 않음");
        var centerFace = new RectangleF(.25f, .25f, .5f, .5f);
        var centered = CharacterVisit.PoseAt(1.1, origin, originalSize, originalAngle, smallArea, centerFace);
        check(centered.Center == new PointF(50, 40), "얼굴이 중앙인 원본은 이미지 중심을 화면 중앙에 배치");
        using var actual = new OrangeWalker(OrangeWalker.Characters[0], 32);
        var previewArea = new Rectangle(0, 0, 800, 450);
        float PixelsAt(double seconds) => CharacterVisit.PoseAt(seconds, origin, 32, 0, previewArea, actual.Face).Size;
        check(PixelsAt(1d / 30) >= 34 && PixelsAt(.1) >= 50, "800×450 원본 얼굴 기준 45% 축소 후에도 초반부터 확대가 보임");
        Console.WriteLine("실제 미리보기 크기(px): " + string.Join(", ", new[] { 0d, 1d / 30, .1, .2, .4, .85, 1.5, 1.61, 1.775, 1.94, 2.05 }
            .Select(seconds => $"{seconds:0.000}s={PixelsAt(seconds):0.0}")));
    }
}
