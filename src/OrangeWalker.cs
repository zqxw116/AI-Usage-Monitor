using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Reflection;
using System.Runtime.InteropServices;

namespace UsageMonitor;

internal readonly record struct OrangePose(PointF Center, float Angle, RectangleF Bounds);
internal sealed record CharacterChoice(string Name, string Resource, bool Flip = false, int Columns = 1, int Rows = 1, int FrameDelay = 100, bool RemoveBackground = false)
{
    internal RectangleF Face { get; init; } = new(.29f, .17f, .57f, .40f);
}

internal sealed class OrangeWalker : IDisposable
{
    internal static readonly CharacterChoice[] Characters =
    {
        new("통통한 아기 펭귄 · 뒤뚱걷기", "ChubbyBabyPenguin.gif", RemoveBackground: true),
        new("오리 · 걷기", "DuckWalkSheet.png", true, 4, 2) { Face = new(.47f, .05f, .49f, .40f) },
        new("아기 펭귄 · 원본", "BabyPenguin.gif", RemoveBackground: true) { Face = new(.35f, .35f, .43f, .30f) },
        new("오징어 모자 펭귄 · 원본", "SquidHatPenguin.gif", RemoveBackground: true) { Face = new(.52f, .29f, .32f, .36f) },
        new("두 고양이 · 걷기", "WalkingCats.gif") { Face = new(.78f, .34f, .20f, .30f) }
    };
    private readonly Bitmap[] frames;
    private readonly int[] frameEnds;
    private float height;
    internal float PreferredSize => height;
    internal int FrameCount => frames.Length;
    internal int CycleMilliseconds => frameEnds[^1];
    internal RectangleF Face { get; }

    public OrangeWalker(CharacterChoice? character = null, float size = 22)
    {
        character ??= Characters[0];
        Face = character.Face;
        height = size;
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(character.Resource)!;
        using var gif = Image.FromStream(stream);
        bool sheet = character.Columns > 1 || character.Rows > 1;
        bool animated = gif.FrameDimensionsList.Contains(FrameDimension.Time.Guid);
        int count = sheet ? character.Columns * character.Rows : animated ? gif.GetFrameCount(FrameDimension.Time) : 1;
        int frameWidth = gif.Width / character.Columns, frameHeight = gif.Height / character.Rows;
        using var sheetImage = sheet ? new Bitmap(gif) : null;
        var decoded = new Bitmap[count];
        var delayBytes = animated && gif.PropertyIdList.Contains(0x5100) ? gif.GetPropertyItem(0x5100)!.Value : null;
        frameEnds = new int[count];
        Rectangle crop = Rectangle.Empty;
        for (int i = 0; i < count; i++)
        {
            if (animated) gif.SelectActiveFrame(FrameDimension.Time, i);
            decoded[i] = sheet
                ? sheetImage!.Clone(new Rectangle(i % character.Columns * frameWidth, i / character.Columns * frameHeight, frameWidth, frameHeight), PixelFormat.Format32bppArgb)
                : new Bitmap(gif);
            if (character.RemoveBackground) RemoveEdgeBackground(decoded[i]);
            var data = decoded[i].LockBits(new Rectangle(0, 0, frameWidth, frameHeight), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            var pixels = new int[data.Stride / 4 * frameHeight];
            Marshal.Copy(data.Scan0, pixels, 0, pixels.Length);
            decoded[i].UnlockBits(data);
            for (int y = 0; y < frameHeight; y++)
            for (int x = 0; x < frameWidth; x++)
            {
                int alpha = (pixels[y * (data.Stride / 4) + x] >> 24) & 0xff;
                // 생성 프레임의 거의 투명한 외곽 픽셀이 표시 영역을 불필요하게 넓히지 않게 합니다.
                if (alpha < (sheet ? 32 : 1)) continue;
                var pixel = new Rectangle(x, y, 1, 1);
                crop = crop.IsEmpty ? pixel : Rectangle.Union(crop, pixel);
            }
            int delay = sheet ? character.FrameDelay : delayBytes is null ? 1000 : Math.Max(10, BitConverter.ToInt32(delayBytes, i * 4) * 10);
            frameEnds[i] = (i == 0 ? 0 : frameEnds[i - 1]) + delay;
        }
        frames = new Bitmap[count];
        for (int i = 0; i < count; i++)
        {
            // 모든 프레임에 공통 영역을 적용해 몸의 움직임을 유지하고 발이 테두리에 닿게 배치합니다.
            int side = Math.Max(crop.Width, crop.Height);
            frames[i] = new Bitmap(side, side, PixelFormat.Format32bppArgb);
            using (var cropped = decoded[i].Clone(crop, PixelFormat.Format32bppArgb))
            using (var graphics = Graphics.FromImage(frames[i]))
                graphics.DrawImageUnscaled(cropped, (side - crop.Width) / 2, side - crop.Height);
            if (character.Flip) frames[i].RotateFlip(RotateFlipType.RotateNoneFlipX);
            decoded[i].Dispose();
        }
    }

    private static void RemoveEdgeBackground(Bitmap bitmap)
    {
        var data = bitmap.LockBits(new Rectangle(0, 0, bitmap.Width, bitmap.Height), ImageLockMode.ReadWrite, PixelFormat.Format32bppArgb);
        int stride = data.Stride / 4;
        var pixels = new int[stride * bitmap.Height];
        Marshal.Copy(data.Scan0, pixels, 0, pixels.Length);
        int background = pixels[0];
        var visited = new bool[pixels.Length];
        var pending = new Queue<int>();
        void Enqueue(int x, int y)
        {
            int index = y * stride + x;
            if (visited[index]) return;
            visited[index] = true;
            int color = pixels[index];
            if (Math.Abs((color & 255) - (background & 255)) > 12 ||
                Math.Abs(((color >> 8) & 255) - ((background >> 8) & 255)) > 12 ||
                Math.Abs(((color >> 16) & 255) - ((background >> 16) & 255)) > 12) return;
            pending.Enqueue(index);
        }
        for (int x = 0; x < bitmap.Width; x++) { Enqueue(x, 0); Enqueue(x, bitmap.Height - 1); }
        for (int y = 0; y < bitmap.Height; y++) { Enqueue(0, y); Enqueue(bitmap.Width - 1, y); }
        while (pending.TryDequeue(out int index))
        {
            pixels[index] = 0;
            int x = index % stride, y = index / stride;
            if (x > 0) Enqueue(x - 1, y);
            if (x + 1 < bitmap.Width) Enqueue(x + 1, y);
            if (y > 0) Enqueue(x, y - 1);
            if (y + 1 < bitmap.Height) Enqueue(x, y + 1);
        }
        Marshal.Copy(pixels, 0, data.Scan0, pixels.Length);
        bitmap.UnlockBits(data);
    }

    internal Bitmap Thumbnail()
    {
        var icon = new Bitmap(24, 24);
        using var graphics = Graphics.FromImage(icon);
        graphics.InterpolationMode = InterpolationMode.NearestNeighbor;
        graphics.PixelOffsetMode = PixelOffsetMode.Half;
        graphics.DrawImage(frames[0], new Rectangle(0, 0, 24, 24));
        return icon;
    }

    internal int FrameAt(double seconds)
    {
        double time = seconds * 1000 % CycleMilliseconds;
        for (int i = 0; i < frameEnds.Length; i++) if (time < frameEnds[i]) return i;
        return 0;
    }

    internal float Height(Size canvas) => height;

    internal void ChangeSize(int delta, Size canvas) =>
        height = Math.Max(Height(canvas) + delta, 8);

    private float TrackInset(Size canvas) => Math.Min(Height(canvas) / 2 + 2, Math.Min(canvas.Width, canvas.Height) / 2f - 2);

    internal double Perimeter(Size canvas, bool outside = false)
    {
        double inset = outside ? -Height(canvas) / 2 : TrackInset(canvas);
        return 2 * (canvas.Width + canvas.Height - 4 * inset);
    }

    internal OrangePose PoseAt(double distance, Size canvas, bool outside = false)
    {
        float spriteHeight = Height(canvas);
        float spriteWidth = spriteHeight * frames[0].Width / frames[0].Height;
        float inset = outside ? -spriteHeight / 2 : TrackInset(canvas);
        float horizontal = canvas.Width - inset * 2, vertical = canvas.Height - inset * 2;
        double length = 2 * (horizontal + vertical);
        float offset = (float)((distance % length + length) % length);
        PointF center;
        float angle;
        if (offset < horizontal)
        {
            center = new PointF(inset + offset, canvas.Height - inset);
            angle = 0;
        }
        else if (offset < horizontal + vertical)
        {
            center = new PointF(canvas.Width - inset, canvas.Height - inset - (offset - horizontal));
            angle = -90;
        }
        else if (offset < horizontal * 2 + vertical)
        {
            center = new PointF(canvas.Width - inset - (offset - horizontal - vertical), inset);
            angle = -180;
        }
        else
        {
            center = new PointF(inset, inset + offset - horizontal * 2 - vertical);
            angle = -270;
        }
        float width = angle is -90 or -270 ? spriteHeight : spriteWidth;
        float tall = angle is -90 or -270 ? spriteWidth : spriteHeight;
        if (outside)
        {
            // 외부에서는 위→오른쪽→아래→왼쪽으로 돌며 발이 창을 향합니다.
            center.Y = canvas.Height - center.Y;
            angle = -angle;
        }
        return new OrangePose(center, angle, new RectangleF(center.X - width / 2, center.Y - tall / 2, width, tall));
    }

    internal void Draw(Graphics graphics, Size canvas, double seconds, double distance)
    {
        var pose = PoseAt(distance, canvas);
        DrawSprite(graphics, pose.Center, pose.Angle, seconds, Height(canvas));
    }

    internal void DrawSprite(Graphics graphics, PointF center, float angle, double seconds, float spriteHeight, bool smooth = false, bool mirrorX = false)
    {
        float spriteWidth = spriteHeight * frames[0].Width / frames[0].Height;
        var saved = graphics.Save();
        graphics.TranslateTransform(center.X, center.Y);
        graphics.RotateTransform(angle);
        if (mirrorX) graphics.ScaleTransform(-1, 1);
        graphics.InterpolationMode = smooth ? InterpolationMode.HighQualityBilinear : InterpolationMode.NearestNeighbor;
        graphics.PixelOffsetMode = smooth ? PixelOffsetMode.HighQuality : PixelOffsetMode.Half;
        graphics.DrawImage(frames[FrameAt(seconds)], new RectangleF(-spriteWidth / 2, -spriteHeight / 2, spriteWidth, spriteHeight));
        graphics.Restore(saved);
    }

    internal bool HitTestSprite(PointF point, PointF center, float angle, double seconds, float size, bool mirrorX = false)
    {
        double radians = angle * Math.PI / 180, dx = point.X - center.X, dy = point.Y - center.Y;
        var frame = frames[FrameAt(seconds)];
        double localX = dx * Math.Cos(radians) + dy * Math.Sin(radians);
        if (mirrorX) localX = -localX;
        int x = (int)Math.Floor((localX / size + .5) * frame.Width);
        int y = (int)Math.Floor(((-dx * Math.Sin(radians) + dy * Math.Cos(radians)) / size + .5) * frame.Height);
        return x >= 0 && x < frame.Width && y >= 0 && y < frame.Height && frame.GetPixel(x, y).A >= 32;
    }

    public void Dispose()
    {
        foreach (var frame in frames) frame.Dispose();
    }
}
