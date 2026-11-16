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
            bool clearOfPlan = true;
            var overlapRows = new HashSet<int>();
            int maxBrandOverlap = 0;
            using var planMask = new Bitmap(74, 50);
            using (var graphics = Graphics.FromImage(planMask))
            using (var font = new Font("Segoe UI", 10, FontStyle.Bold, GraphicsUnit.Pixel))
            {
                graphics.Clear(background);
                graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
                typeof(MonitorForm).GetMethod("DrawText", BindingFlags.Static | BindingFlags.NonPublic)!
                    .Invoke(null, new object[] { graphics, name == "Codex" ? "Pro" : "Max 20x", font, Color.FromArgb(207, 217, 228), new RectangleF(2, 38, 68, 10), true });
            }
            for (int step = 0; step < 90; step++)
            {
                using var frame = Render(form, name, step * .02, true);
                for (int y = 38; y < frame.Height; y++)
                    for (int x = 0; x < frame.Width; x++)
                    {
                        bool overlap = frame.GetPixel(x, y).ToArgb() != background.ToArgb()
                            && planMask.GetPixel(x, y).ToArgb() != background.ToArgb();
                        clearOfPlan &= !overlap;
                        if (overlap)
                        {
                            overlapRows.Add(y);
                            maxBrandOverlap = Math.Max(maxBrandOverlap, Math.Abs(frame.GetPixel(x, y).G - background.G));
                        }
                    }
            }
            Console.WriteLine($"{name}: overlap rows={string.Join(',', overlapRows)}, max green coverage={maxBrandOverlap}/183");
            Check(clearOfPlan, name + " 요금제 실제 글자 픽셀과 겹침 없음");
        }
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

    private static Bitmap Render(MonitorForm form, string name, double phase, bool plan)
    {
        var bitmap = new Bitmap(74, 50, PixelFormat.Format32bppPArgb);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.Clear(background);
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

    private static void CheckCache(MonitorForm form)
    {
        var cache = (System.Collections.IDictionary)typeof(MonitorForm).GetField("brandGlyphs", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(form)!;
        var images = cache.Values.Cast<Array>().SelectMany(glyphs => glyphs.Cast<object>())
            .Select(glyph => (Bitmap)glyph.GetType().GetField("Item1")!.GetValue(glyph)!).ToArray();
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
        bool disposed = images.All(image =>
        {
            try { image.GetPixel(0, 0); return false; }
            catch (ArgumentException) { return true; }
        });
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
