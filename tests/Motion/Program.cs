using System.Drawing;
using System.Drawing.Imaging;
using System.Reflection;
using System.Runtime.InteropServices;
using UsageMonitor;

internal static class Program
{
    private static int failed;
    private static readonly MethodInfo drawBrand = typeof(MonitorForm).GetMethod("DrawBrand", BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static readonly Color background = Color.FromArgb(32, 38, 46);
    private static readonly Color ink = Color.FromArgb(81, 221, 190);
    private static void Check(bool passed, string name)
    {
        Console.WriteLine((passed ? "PASS " : "FAIL ") + name);
        if (!passed) failed++;
    }

    [STAThread]
    private static int Main(string[] args)
    {
        legacy = args.Contains("--legacy");
        using var codex = new UsageSource(false);
        using var claude = new UsageSource(true);
        var settings = new MonitorSettings();
        using var form = new MonitorForm(codex, claude, false, settings);
        foreach (string name in new[] { "Codex", "Claude" })
        {
            int changed = 0;
            int comparisons = 0;
            using var first = Render(form, name, 0, true);
            Bitmap previous = (Bitmap)first.Clone();
            for (int step = 1; step <= 90; step++)
            {
                using var next = Render(form, name, step * .02, true);
                if (!Equal(previous, next)) changed++;
                comparisons++;
                previous.Dispose();
                previous = (Bitmap)next.Clone();
            }
            previous.Dispose();
            Console.WriteLine($"{name}: 20ms frame changes {changed}/{comparisons}");
            Check(changed == comparisons, name + " 20ms 간격의 서브픽셀 모션");
            CheckPlanSpacing(form, name);
        }
        if (!legacy) CheckScaling(form);
        settings.VisualEffects = false;
        effects = false;
        using var offA = Render(form, "Codex", 0, true);
        using var offB = Render(form, "Codex", .4, true);
        Check(Equal(offA, offB), "효과 끄면 레이블 정지");
        if (!legacy) CheckCache(form);
        Console.WriteLine($"RESULT failed={failed}");
        return failed == 0 ? 0 : 1;
    }

    private static bool legacy;
    private static bool effects = true;

    private static void CheckPlanSpacing(MonitorForm form, string name)
    {
        foreach (string plan in new[] { "Pro Lite", "Max 20x", "Team", "Enterprise" })
        {
            using var planMask = new Bitmap(74, 50);
            using (var graphics = Graphics.FromImage(planMask))
            using (var font = new Font("Segoe UI", 8.5f, FontStyle.Bold, GraphicsUnit.Pixel))
            {
                graphics.Clear(background);
                graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
                typeof(MonitorForm).GetMethod("DrawText", BindingFlags.Static | BindingFlags.NonPublic)!
                    .Invoke(null, new object[] { graphics, plan, font, Color.FromArgb(207, 217, 228), new RectangleF(2, 39, 68, 8), true });
            }
            var planPixels = new List<Point>();
            for (int y = 0; y < planMask.Height; y++)
                for (int x = 0; x < planMask.Width; x++)
                    if (planMask.GetPixel(x, y).ToArgb() != background.ToArgb()) planPixels.Add(new Point(x, y));
            bool clearOfPlan = true;
            for (int step = 0; step < 90; step++)
            {
                using var frame = Render(form, name, step * .02, true);
                clearOfPlan &= planPixels.All(point => frame.GetPixel(point.X, point.Y).ToArgb() == background.ToArgb());
            }
            int firstRow = planPixels.Count == 0 ? -1 : planPixels.Min(point => point.Y);
            int lastRow = planPixels.Count == 0 ? -1 : planPixels.Max(point => point.Y);
            Console.WriteLine($"{name}/{plan}: plan ink rows={firstRow}..{lastRow}");
            Check(clearOfPlan && planPixels.Count > 0, name + "/" + plan + " 브랜드와 실제 글자 겹침 없음");
            Check(planPixels.Count > 0 && lastRow < 47, name + "/" + plan + " 하단 테두리(y>=47) 침범 없음");
        }
    }

    private static Bitmap Render(MonitorForm form, string name, double phase, bool plan, float scale = 1)
    {
        var bitmap = new Bitmap((int)Math.Ceiling(74 * scale), (int)Math.Ceiling(50 * scale), PixelFormat.Format32bppPArgb);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.Clear(background);
        graphics.ScaleTransform(scale, scale);
        graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
        var area = new RectangleF(3, plan ? 28 : 31, 66, 13);
        if (legacy && effects)
        {
            using var font = new Font("Segoe UI", 11, FontStyle.Bold, GraphicsUnit.Pixel);
            using var format = (StringFormat)StringFormat.GenericTypographic.Clone();
            using var brush = new SolidBrush(ink);
            var widths = name.Select(c => graphics.MeasureString(c.ToString(), font, PointF.Empty, format).Width).ToArray();
            float x = area.X + (area.Width - widths.Sum()) / 2;
            float y = area.Y + (area.Height - font.GetHeight(graphics)) / 2;
            for (int i = 0; i < name.Length; i++)
            {
                float offset = (plan ? 1.3f : 2f) * (float)-Math.Sin(phase * Math.PI * 2 / 1.8 - i * .65);
                graphics.DrawString(name[i].ToString(), font, brush, new PointF(x, y + offset), format);
                x += widths[i];
            }
        }
        else drawBrand.Invoke(form, new object[] { graphics, name, ink, area, phase, plan });
        return bitmap;
    }

    private static System.Collections.IDictionary Cache(MonitorForm form) =>
        (System.Collections.IDictionary)typeof(MonitorForm).GetField("brandGlyphs", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(form)!;

    private static Bitmap[] Images(MonitorForm form) => Cache(form).Values.Cast<Array>()
        .SelectMany(glyphs => glyphs.Cast<object>())
        .Select(glyph => (Bitmap)glyph.GetType().GetField("Item1")!.GetValue(glyph)!).ToArray();

    private static bool Disposed(Bitmap image)
    {
        try { image.GetPixel(0, 0); return false; }
        catch (ArgumentException) { return true; }
    }

    private static void CheckScaling(MonitorForm form)
    {
        void Populate(float scale)
        {
            using var codex = Render(form, "Codex", .2, true, scale);
            using var claude = Render(form, "Claude", .2, true, scale);
        }
        Populate(1);
        var logicalSizes = Images(form).Select(image => image.Size).ToArray();
        foreach (int scale in new[] { 2, 3 })
        {
            var old = Images(form);
            Populate(scale);
            var resized = Images(form);
            bool proportional = resized.Length == logicalSizes.Length;
            for (int i = 0; i < Math.Min(resized.Length, logicalSizes.Length); i++)
                proportional &= Math.Abs(resized[i].Width - logicalSizes[i].Width * scale) <= scale
                    && Math.Abs(resized[i].Height - logicalSizes[i].Height * scale) <= scale;
            Console.WriteLine($"scale={scale}: first glyph={resized[0].Width}x{resized[0].Height}, base={logicalSizes[0].Width}x{logicalSizes[0].Height}");
            Check(proportional, $"{scale}배 확대 글리프 캐시가 실제 해상도에 비례");
            Check(old.All(Disposed), $"{scale}배 확대 시 이전 배율 캐시 해제");
            using var first = Render(form, "Codex", .2, true, scale);
            using var second = Render(form, "Codex", .22, true, scale);
            Check(!Equal(first, second), $"{scale}배 확대에서도 20ms 서브픽셀 모션 유지");
        }
        Populate(1.01f);
        var sameBucket = Images(form);
        Populate(1.2f);
        Check(sameBucket.SequenceEqual(Images(form)), "같은 0.25 배율 구간에서 캐시 재사용");
        bool bounded = true;
        bool previousDisposed = true;
        long maxBytes = 0;
        for (int index = 0; index < 30; index++)
        {
            var previous = Images(form);
            Populate(1 + index % 9 * .25f);
            var current = Images(form);
            previousDisposed &= previous.All(Disposed);
            long bytes = current.Sum(image => (long)image.Width * image.Height * 4);
            maxBytes = Math.Max(maxBytes, bytes);
            bounded &= current.Length == 11 && bytes < 16384 * 9;
        }
        Check(previousDisposed, "연속 크기 변경 시 교체된 모든 Bitmap 해제");
        Check(bounded, $"30회 크기 변경 후 현재 배율 캐시만 보관 (최대 {maxBytes}바이트)");
        Populate(1);
    }

    private static void CheckCache(MonitorForm form)
    {
        var cache = Cache(form);
        var images = Images(form);
        bool premultiplied = true;
        bool padded = true;
        int bytes = 0;
        foreach (var image in images)
        {
            bytes += image.Width * image.Height * 4;
            var data = image.LockBits(new Rectangle(0, 0, image.Width, image.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppPArgb);
            var pixels = new byte[data.Stride * image.Height];
            Marshal.Copy(data.Scan0, pixels, 0, pixels.Length);
            image.UnlockBits(data);
            for (int y = 0; y < image.Height; y++)
                for (int x = 0; x < image.Width; x++)
                {
                    int at = y * data.Stride + x * 4;
                    int alpha = pixels[at + 3];
                    premultiplied &= Math.Abs(pixels[at] - ink.B * alpha / 255.0) <= 1
                        && Math.Abs(pixels[at + 1] - ink.G * alpha / 255.0) <= 1
                        && Math.Abs(pixels[at + 2] - ink.R * alpha / 255.0) <= 1;
                    if (x == 0 || y == 0 || x == image.Width - 1 || y == image.Height - 1) padded &= alpha == 0;
                }
        }
        Check(premultiplied, "캐시의 투명 경계 RGB가 알파에 비례하여 밝은 외곽 없음");
        Check(padded, "보간용 글리프 가장자리 투명 패딩");
        Check(images.Length == 11 && bytes < 16384, $"작은 캐시 재사용: {images.Length}개, {bytes}바이트");
        form.Dispose();
        bool disposed = images.All(Disposed);
        Check(disposed && cache.Count == 0, "창 해제 시 캐시 Bitmap Dispose 및 비우기");
    }

    private static bool Equal(Bitmap a, Bitmap b)
    {
        for (int y = 0; y < a.Height; y++)
            for (int x = 0; x < a.Width; x++)
                if (a.GetPixel(x, y) != b.GetPixel(x, y)) return false;
        return true;
    }
}
