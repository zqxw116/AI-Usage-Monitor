using System.Drawing;
using System.Drawing.Drawing2D;
using System.Reflection;
using UsageMonitor;

internal static class PremiumPaletteChecks
{
    internal static void Run(Action<bool, string> check)
    {
        var context = SynchronizationContext.Current;
        try { RunChecks(check); }
        finally { SynchronizationContext.SetSynchronizationContext(context); }
    }

    private static void RunChecks(Action<bool, string> check)
    {
        const BindingFlags flags = BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public;
        var classifier = typeof(MonitorForm).GetMethod("UsesPremiumPalette", flags);
        check(classifier is not null, "프리미엄 팔레트 등급 분류 제공");
        if (classifier is not null)
        {
            bool Premium(ServiceKind kind, Snapshot? data) => (bool)classifier.Invoke(null, new object?[] { kind, data })!;
            foreach (var kind in Enum.GetValues<ServiceKind>()) check(!Premium(kind, null), kind + " 미확인 등급은 표준 팔레트");
            foreach (string plan in new[] { "Pro", "Pro Lite" })
                check(Premium(ServiceKind.Codex, Data(plan, true)), "Codex " + plan + " 프리미엄 팔레트");
            foreach (string plan in new[] { "Free", "Plus", "Team", "Enterprise" })
                check(!Premium(ServiceKind.Codex, Data(plan)), "Codex " + plan + " 표준 팔레트");
            foreach (string plan in new[] { "Max", "Max 5x", "Max 20x" })
                check(Premium(ServiceKind.Claude, Data(plan)), "Claude " + plan + " 프리미엄 팔레트");
            foreach (string plan in new[] { "Free", "Pro", "Team", "Enterprise", "Maximus", "" })
                check(!Premium(ServiceKind.Claude, Data(plan)), "Claude " + plan + " 표준 팔레트");
            check(!Premium(ServiceKind.Gemini, Data("Google AI Ultra", true)), "Gemini 등급은 이번 프리미엄 팔레트 대상 아님");
        }
        var proFill = typeof(MonitorForm).GetMethod("ProFill", flags)!;
        Color[] Palette(bool low)
        {
            object[] args = proFill.GetParameters().Length == 5
                ? new object[] { 0f, 0f, 198f, MonitorForm.RemainingColor(100, false), low }
                : new object[] { 0f, 0f, 198f, MonitorForm.RemainingColor(100, false) };
            using var brush = (LinearGradientBrush)proFill.Invoke(null, args)!;
            return brush.InterpolationColors.Colors;
        }
        var full = Palette(false);
        var low = Palette(true);
        check(full.Select(c => c.ToArgb()).SequenceEqual(low.Select(c => c.ToArgb())), "프리미엄 잔량 20% 이하에서도 100% 무지개 팔레트 유지");
        check(full.Length >= 5 && full.Any(c => c.B > 200 && c.R < 150)
            && full.Any(c => c.R > 200 && c.B > 200), "프리미엄 팔레트 청록·보라·분홍 유지");
        foreach (bool claude in new[] { false, true })
            check(new[] { 100d, 70, 50, 30, 10 }.Select(value => MonitorForm.RemainingColor(value, claude).ToArgb()).Distinct().Count() == 5,
                (claude ? "Claude" : "Codex") + " 표준 잔량 5색 단계 유지");

        using var codex = new UsageSource(false);
        using var claudeSource = new UsageSource(true);
        var settings = new MonitorSettings();
        using var form = new MonitorForm(codex, claudeSource, false, settings);
        foreach (var kind in new[] { ServiceKind.Codex, ServiceKind.Claude })
        {
            using var high = Render(form, settings, kind, true, 100, false, 0);
            foreach (int remaining in new[] { 70, 50, 30, 20, 10 })
            {
                using var reduced = Render(form, settings, kind, true, remaining, false, 0);
                bool same = true;
                foreach (float fraction in new[] { .2f, .4f, .6f, .8f })
                {
                    float length = 198 * remaining / 100f;
                    int smallX = 1 + (int)(length * fraction);
                    float actualFraction = (smallX + .5f - 1) / length;
                    int largeX = Math.Clamp((int)Math.Round(1 + 198 * actualFraction - .5f), 2, 197);
                    var a = high.GetPixel(largeX, 35);
                    var b = reduced.GetPixel(smallX, 35);
                    if (Math.Abs(a.R - b.R) > 24 || Math.Abs(a.G - b.G) > 24 || Math.Abs(a.B - b.B) > 24)
                        Console.WriteLine($"{kind} {remaining}% fraction={actualFraction:F3}: full={a.R},{a.G},{a.B}, reduced={b.R},{b.G},{b.B}");
                    same &= Math.Abs(a.R - b.R) <= 24 && Math.Abs(a.G - b.G) <= 24 && Math.Abs(a.B - b.B) <= 24;
                }
                check(same, kind + $" 프리미엄 {remaining}% 바의 무지개 색 유지");
            }
            double standard = WhiteCoverage(form, settings, kind, false);
            double premium = WhiteCoverage(form, settings, kind, true);
            Console.WriteLine($"{kind}: normalized white coverage standard={standard:F4}, premium={premium:F4}");
            check(premium > 0 && standard < premium * .75, kind + " 표준 빛물결은 프리미엄 대비 75% 미만");
        }
    }

    private static Snapshot Data(string plan, bool codexPro = false) => new(null, null, DateTimeOffset.UtcNow, CodexPro: codexPro, PlanLabel: plan);

    private static readonly MethodInfo drawQuota = typeof(MonitorForm).GetMethods(BindingFlags.Instance | BindingFlags.NonPublic)
        .Single(method => method.Name == "DrawQuota" && method.GetParameters().Length == 9);

    private static Bitmap Render(MonitorForm form, MonitorSettings settings, ServiceKind kind, bool premium, int remaining, bool effects, double time)
    {
        settings.VisualEffects = effects;
        form.PreviewOrange(0, time);
        var bitmap = new Bitmap(210, 50);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.Clear(Color.FromArgb(32, 38, 46));
        drawQuota.Invoke(form, new object?[] { graphics, new Quota(100 - remaining, null), 0, 0, "잔량", kind, false, 200, premium });
        return bitmap;
    }

    private static double WhiteCoverage(MonitorForm form, MonitorSettings settings, ServiceKind kind, bool premium)
    {
        double total = 0;
        int count = 0;
        using var baseline = Render(form, settings, kind, premium, 100, false, 0);
        foreach (double phase in new[] { 0d, .4, .8, 1.2, 1.6, 2 })
        {
            using var animated = Render(form, settings, kind, premium, 100, true, phase);
            for (int y = 33; y <= 37; y++)
                for (int x = 3; x < 197; x++)
                {
                    Color a = baseline.GetPixel(x, y), b = animated.GetPixel(x, y);
                    foreach (var pair in new[] { (a.R, b.R), (a.G, b.G), (a.B, b.B) })
                        if (pair.Item1 < 235)
                        {
                            total += Math.Clamp((pair.Item2 - pair.Item1) / (double)(255 - pair.Item1), 0, 1);
                            count++;
                        }
                }
        }
        return total / count;
    }
}
