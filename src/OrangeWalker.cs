using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Reflection;
using System.Runtime.InteropServices;

namespace UsageMonitor;

internal readonly record struct OrangePose(PointF Center, float Angle, RectangleF Bounds);
internal sealed record CharacterChoice(string Name, string Resource, bool Flip = false, int Columns = 1, int Rows = 1, int FrameDelay = 100, float Scale = 1, Rectangle? Crop = null);

internal sealed class OrangeWalker : IDisposable
{
    internal static readonly CharacterChoice[] Characters =
    {
        new("오렌지", "WalkingOrange.gif"), new("비행선", "Dirigible.gif"),
        new("빨간 자동차", "RedCar.gif", true, Scale: 1.5f, Crop: new Rectangle(50, 55, 74, 36)), new("아기 낙타", "BabyCamel.gif"),
        new("오리 · 걷기", "DuckWalkSheet.png", true, 4, 2), new("커피 새 · 걷기", "CoffeeWalkSheet.png", false, 4, 2)
    };
    private readonly Bitmap[] frames;
    private readonly int[] frameEnds;
    private float height;
    private readonly float scale;
    internal float PreferredSize => height;
    internal int FrameCount => frames.Length;
    internal int CycleMilliseconds => frameEnds[^1];

    public OrangeWalker(CharacterChoice? character = null, float size = 22)
    {
        character ??= Characters[0];
        height = size;
        scale = character.Scale;
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
        if (character.Crop is { } focus) crop = Rectangle.Intersect(crop, focus);
        frames = new Bitmap[count];
        for (int i = 0; i < count; i++)
        {
            // 가로로 긴 자동차·낙타도 비율을 유지하고 발/바퀴가 테두리에 닿도록 투명 정사각형의 아래에 배치합니다.
            int side = Math.Max(crop.Width, crop.Height);
            frames[i] = new Bitmap(side, side, PixelFormat.Format32bppArgb);
            using (var cropped = decoded[i].Clone(crop, PixelFormat.Format32bppArgb))
            using (var graphics = Graphics.FromImage(frames[i]))
                graphics.DrawImageUnscaled(cropped, (side - crop.Width) / 2, side - crop.Height);
            if (character.Flip) frames[i].RotateFlip(RotateFlipType.RotateNoneFlipX);
            decoded[i].Dispose();
        }
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

    internal float Height(Size canvas) => Math.Min(height * scale, Math.Min(64, Math.Min(canvas.Width, canvas.Height) - 8));

    internal void ChangeSize(int delta, Size canvas) =>
        height = Math.Clamp(Height(canvas) + delta, 8, Math.Min(64, Math.Min(canvas.Width, canvas.Height) - 8)) / scale;

    internal double Perimeter(Size canvas)
    {
        double inset = Height(canvas) / 2 + 2;
        return 2 * (canvas.Width + canvas.Height - 4 * inset);
    }

    internal OrangePose PoseAt(double distance, Size canvas)
    {
        float spriteHeight = Height(canvas);
        float spriteWidth = spriteHeight * frames[0].Width / frames[0].Height;
        float inset = spriteHeight / 2 + 2;
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
        return new OrangePose(center, angle, new RectangleF(center.X - width / 2, center.Y - tall / 2, width, tall));
    }

    internal void Draw(Graphics graphics, Size canvas, double seconds, double distance)
    {
        var pose = PoseAt(distance, canvas);
        float spriteHeight = Height(canvas);
        float spriteWidth = spriteHeight * frames[0].Width / frames[0].Height;
        var saved = graphics.Save();
        graphics.TranslateTransform(pose.Center.X, pose.Center.Y);
        graphics.RotateTransform(pose.Angle);
        graphics.InterpolationMode = InterpolationMode.NearestNeighbor;
        graphics.PixelOffsetMode = PixelOffsetMode.Half;
        graphics.DrawImage(frames[FrameAt(seconds)], new RectangleF(-spriteWidth / 2, -spriteHeight / 2, spriteWidth, spriteHeight));
        graphics.Restore(saved);
    }

    public void Dispose()
    {
        foreach (var frame in frames) frame.Dispose();
    }
}
