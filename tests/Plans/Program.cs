using System.Text.Json;
using UsageMonitor;

int failed = 0;
void Check(bool value, string name)
{
    Console.WriteLine((value ? "PASS " : "FAIL ") + name);
    if (!value) failed++;
}
Snapshot Codex(string json)
{
    using var document = JsonDocument.Parse(json);
    return UsageParser.Parse(document.RootElement, false, DateTimeOffset.UtcNow);
}
string? Claude(string json)
{
    using var document = JsonDocument.Parse(json);
    return UsageParser.ClaudePlan(document.RootElement);
}
string? Gemini(string json)
{
    using var document = JsonDocument.Parse(json);
    return GeminiApi.ParsePlan(document.RootElement);
}
Check(Codex("""{"plan_type":"plus"}""").PlanLabel == "Plus", "Codex Plus uses explicit response plan");
Check(Codex("""{"plan_type":"pro"}""") is { PlanLabel: "Pro", CodexPro: true }, "Codex Pro preserves existing behavior");
Check(Codex("""{"plan_type":"prolite"}""").PlanLabel == "Pro Lite", "Codex Pro Lite remains distinct");
Check(Codex("""{"plan_type":"future"}""").PlanLabel is null, "Unknown Codex plan stays unknown");
Check(Codex("{}").PlanLabel is null, "Missing Codex plan stays unknown");
Check(Claude("""{"subscriptionType":"max","rateLimitTier":"default_claude_max_20x"}""") == "Max 20x", "Claude explicit Max tier");
Check(Claude("""{"subscriptionType":"max"}""") == "Max", "Claude Max without multiplier does not guess");
Check(Claude("""{"subscriptionType":"pro"}""") == "Pro", "Claude Pro metadata");
Check(Claude("""{"rateLimitTier":"default_claude_max_20x"}""") is null, "Claude multiplier cannot imply subscription");
Check(Claude("""{"subscriptionType":"future"}""") is null, "Unknown Claude subscription stays unknown");
using (var desktop = JsonDocument.Parse("""{"subscription_type":"pro","five_hour":{"utilization":20}}"""))
    Check(UsageParser.Parse(desktop.RootElement, true, DateTimeOffset.UtcNow).PlanLabel == "Pro", "Desktop explicit usage subscription type");
using (var desktop = JsonDocument.Parse("""{"plan_type":"max"}"""))
    Check(UsageParser.ClaudeResponsePlan(desktop.RootElement) == "Max", "Desktop explicit response plan type");
using (var desktop = JsonDocument.Parse("""{"plan_type":"future"}"""))
    Check(UsageParser.ClaudeResponsePlan(desktop.RootElement) is null, "Desktop unknown plan stays unknown");
Check(Gemini("""{"currentTier":{"id":"free-tier","name":"Free"},"paidTier":{"id":"g1-pro-tier","name":"Google AI Pro"}}""") == "Google AI Pro", "Gemini paid tier precedes current tier");
Check(Gemini("""{"currentTier":{"id":"free-tier"}}""") == "Free", "Gemini explicit free tier");
Check(Gemini("""{"allowedTiers":[{"name":"Google AI Ultra"}]}""") is null, "Eligible tiers do not imply subscription");
Check(Gemini("""{"currentTier":{"name":"bad\nlabel"}}""") is null, "Gemini multiline name rejected");
Check(Gemini("{}") is null, "Missing Gemini metadata stays unknown");
string directory = Path.Combine(Path.GetTempPath(), "AIUsageMonitor-plans-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(directory);
try
{
    File.WriteAllText(Path.Combine(directory, ".credentials.json"),
        """{"claudeAiOauth":{"accessToken":"synthetic-token","subscriptionType":"max","rateLimitTier":"default_claude_max_5x"}}""");
    using var client = new HttpClient(new SyntheticUsageHandler());
    using var source = new UsageSource(true, client, directory, () => null);
    await source.TickAsync(true);
    Check(source.Data?.PlanLabel == "Max 5x" && !source.Failed, "Claude Code metadata reaches fetched snapshot");
    using var desktopClient = new HttpClient(new SyntheticDesktopHandler());
    using var desktopSource = new UsageSource(true, desktopClient, directory,
        () => new ClaudeDesktopCredential { SessionKey = "synthetic-session" }, desktopOnly: true);
    await desktopSource.TickAsync(true);
    Check(desktopSource.Data?.PlanLabel == "Team" && !desktopSource.Failed, "Desktop selected organization explicit plan reaches snapshot");
}
finally
{
    // Only the randomly named synthetic fixture directory created above is removed.
    Directory.Delete(directory, true);
}
Console.WriteLine($"RESULT failed={failed}");
return failed == 0 ? 0 : 1;

sealed class SyntheticUsageHandler : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) =>
        Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
        {
            Content = new StringContent("""{"five_hour":{"utilization":20}}""")
        });
}

sealed class SyntheticDesktopHandler : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) =>
        Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
        {
            Content = new StringContent(request.RequestUri?.AbsolutePath == "/api/organizations"
                ? """[{"uuid":"b7ee2e9a-2e56-4859-90f2-758a86b47701","plan_type":"team"}]"""
                : """{"five_hour":{"utilization":20}}""")
        });
}
