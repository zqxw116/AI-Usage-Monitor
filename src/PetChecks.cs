namespace UsageMonitor;

internal static class PetChecks
{
    internal static void Run(string directory, Action<bool, string> assert)
    {
        assert(OrangeWalker.Characters.Select(item => item.Resource).SequenceEqual(new[]
        {
            "ChubbyBabyPenguin.gif", "DuckWalkSheet.png",
            "BabyPenguin.gif", "SquidHatPenguin.gif"
        }), "기본 펭귄과 오리·기존 원본 펭귄 둘 제공");
        Directory.CreateDirectory(directory);
        for (int selection = 0; selection < OrangeWalker.Characters.Length; selection++)
        {
            var choice = OrangeWalker.Characters[selection];
            using var character = new OrangeWalker(choice, 64);
            bool sheet = choice.Columns > 1 || choice.Rows > 1;
            using var sourceStream = System.Reflection.Assembly.GetExecutingAssembly().GetManifestResourceStream(choice.Resource)!;
            using var source = Image.FromStream(sourceStream);
            int count = sheet ? choice.Columns * choice.Rows : source.GetFrameCount(System.Drawing.Imaging.FrameDimension.Time);
            var delays = sheet ? null : source.GetPropertyItem(0x5100)!.Value!;
            var starts = new int[count + 1];
            for (int frame = 0; frame < count; frame++)
            {
                int duration = sheet ? choice.FrameDelay : Math.Max(10, BitConverter.ToInt32(delays!, frame * 4) * 10);
                starts[frame + 1] = starts[frame] + duration;
                assert(character.FrameAt(starts[frame] / 1000d + 0.0001) == frame &&
                    character.FrameAt(starts[frame + 1] / 1000d - 0.0001) == frame,
                    choice.Name + " 프레임 " + frame + " 원본 재생 지연 유지");
            }
            assert(character.FrameCount == count && count == 8 && character.CycleMilliseconds == starts[^1] &&
                character.FrameAt(starts[^1] / 1000d) == 0,
                choice.Name + " 원본 8프레임과 반복 주기 유지");
            using var first = new Bitmap(160, 100);
            using var later = new Bitmap(160, 100);
            using (var graphics = Graphics.FromImage(first)) character.Draw(graphics, first.Size, 0, 46);
            using (var graphics = Graphics.FromImage(later)) character.Draw(graphics, later.Size, starts[3] / 1000d + 0.0001, 46);
            int difference = 0, transparent = 0, opaque = 0;
            for (int y = 0; y < first.Height; y++) for (int x = 0; x < first.Width; x++)
            {
                var pixel = first.GetPixel(x, y);
                if (pixel != later.GetPixel(x, y)) difference++;
                if (x >= 46 && x < 110 && y >= 34 && y < 98)
                {
                    if (pixel.A == 0) transparent++;
                    if (pixel.A >= 240) opaque++;
                }
            }
            assert(difference > 0, choice.Name + " 동일 위치에서 몸·다리 동작 변화");
            first.Save(Path.Combine(directory, "펫 " + selection + " 첫 프레임.png"));
            later.Save(Path.Combine(directory, "펫 " + selection + " 동작 프레임.png"));
            assert(transparent > 500 && opaque > 100 && (!sheet || choice.Flip || first.GetPixel(80, 70).A == 255),
                choice.Name + " 주변 배경 투명화와 몸 내부 보존");
            using var strip = new Bitmap(160 * 8, 240);
            var frameHashes = new HashSet<string>();
            using (var graphics = Graphics.FromImage(strip))
            {
                graphics.FillRectangle(Brushes.White, 0, 200, strip.Width, 40);
                using var small = new OrangeWalker(choice, 22);
                for (int frame = 0; frame < 8; frame++)
                {
                    using var image = new Bitmap(160, 100);
                    using (var frameGraphics = Graphics.FromImage(image)) character.Draw(frameGraphics, image.Size, starts[frame] / 1000d + 0.001, 46);
                    assert(image.GetPixel(50, 40).A == 0 && (!sheet || choice.Flip || image.GetPixel(80, 70).A == 255),
                        choice.Name + " 프레임 " + frame + " 배경 투명·몸 내부 보존");
                    graphics.FillRectangle(Brushes.White, frame * 160, 0, 160, 100);
                    graphics.FillRectangle(Brushes.DarkSlateGray, frame * 160, 100, 160, 100);
                    graphics.DrawImageUnscaled(image, frame * 160, 0);
                    graphics.DrawImageUnscaled(image, frame * 160, 100);
                    using var encoded = new MemoryStream();
                    image.Save(encoded, System.Drawing.Imaging.ImageFormat.Png);
                    frameHashes.Add(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(encoded.ToArray())));
                    using var actualSize = new Bitmap(160, 40);
                    using (var actualGraphics = Graphics.FromImage(actualSize)) small.Draw(actualGraphics, actualSize.Size, starts[frame] / 1000d + 0.001, 46);
                    graphics.DrawImageUnscaled(actualSize, frame * 160, 200);
                }
            }
            assert(frameHashes.Count == 8, choice.Name + " 여덟 보행 프레임이 각각 다름");
            strip.Save(Path.Combine(directory, "펫 " + selection + " 전체 프레임.png"));
            foreach (var canvas in new[] { new Size(1094, 50), new Size(594, 100) })
            foreach (int delta in new[] { -100, 14, 100 })
            {
                character.ChangeSize(delta, canvas);
                double loop = character.Perimeter(canvas);
                for (int i = 0; i < 500; i++)
                {
                    var pose = character.PoseAt(loop * i / 500, canvas);
                    if (!float.IsFinite(pose.Center.X) || !float.IsFinite(pose.Center.Y) || pose.Center.X < 1 || pose.Center.Y < 1 || pose.Center.X > canvas.Width - 1 || pose.Center.Y > canvas.Height - 1)
                        throw new InvalidOperationException("캐릭터 회전·잘림 검증 실패");
                }
                assert(character.PoseAt(0, canvas) == character.PoseAt(loop, canvas), choice.Name + " 한 바퀴 뒤 위치·방향 복귀");
                float inset = character.PoseAt(0, canvas).Center.X;
                double horizontal = canvas.Width - inset * 2, vertical = canvas.Height - inset * 2;
                var poses = new[] { character.PoseAt(horizontal / 2, canvas), character.PoseAt(horizontal + vertical / 2, canvas),
                    character.PoseAt(horizontal * 1.5 + vertical, canvas), character.PoseAt(horizontal * 2 + vertical * 1.5, canvas) };
                assert(poses.Select(pose => pose.Angle).SequenceEqual(new[] { 0f, -90f, -180f, -270f }), choice.Name + " 네 변 회전 유지");
            }
            using var enlarged = new OrangeWalker(choice);
            enlarged.ChangeSize(1000, new Size(1094, 50));
            assert(enlarged.Height(new Size(1094, 50)) == 1022 && enlarged.Perimeter(new Size(1094, 50)) > 0,
                choice.Name + " 기존 크기 상한을 넘어 확대하고 이동 유지");
        }
    }
}
