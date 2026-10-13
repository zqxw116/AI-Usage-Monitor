using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace UsageMonitor;

internal sealed record Quota(double Used, DateTimeOffset? Reset)
{
    public double Remaining => 100 - Used;
    public bool Expired => Reset is { } date && date <= DateTimeOffset.UtcNow;
}

internal sealed record Snapshot(Quota? FiveHour, Quota? Weekly, DateTimeOffset Retrieved);

internal static class UsageParser
{
    public static JsonElement Property(JsonElement item, string name) =>
        item.ValueKind == JsonValueKind.Object && item.TryGetProperty(name, out var value) ? value : default;

    public static Quota? ParseWindow(JsonElement window, string usedKey, string resetKey, bool unix)
    {
        var used = Property(window, usedKey);
        if (used.ValueKind != JsonValueKind.Number || !used.TryGetDouble(out var percent) ||
            !double.IsFinite(percent) || percent < 0 || percent > 100) return null;
        DateTimeOffset? reset = null;
        var value = Property(window, resetKey);
        if (unix && value.TryGetInt64Safe(out var seconds))
        {
            try { reset = DateTimeOffset.FromUnixTimeSeconds(seconds); } catch (ArgumentOutOfRangeException) { }
        }
        else if (!unix && value.ValueKind == JsonValueKind.String &&
                 DateTimeOffset.TryParse(value.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
            reset = date;
        return new Quota(percent, reset);
    }

    private static bool TryGetInt64Safe(this JsonElement element, out long value)
    {
        value = 0;
        return element.ValueKind == JsonValueKind.Number && element.TryGetInt64(out value);
    }

    public static Snapshot Parse(JsonElement root, bool claude, DateTimeOffset timestamp)
    {
        if (claude) return new Snapshot(
            ParseWindow(Property(root, "five_hour"), "utilization", "resets_at", false),
            ParseWindow(Property(root, "seven_day"), "utilization", "resets_at", false), timestamp);
        var limits = Property(root, "rate_limit");
        Quota? five = null, week = null;
        foreach (var key in new[] { "primary_window", "secondary_window" })
        {
            var window = Property(limits, key);
            var seconds = Property(window, "limit_window_seconds");
            if (!seconds.TryGetInt64Safe(out var duration)) continue;
            var quota = ParseWindow(window, "used_percent", "reset_at", true);
            if (duration == 18000) five = quota;
            if (duration == 604800) week = quota;
        }
        return new Snapshot(five, week, timestamp);
    }
}

internal sealed class UsageSource : IDisposable
{
    private static readonly HttpClient Client = new() { Timeout = TimeSpan.FromSeconds(15) };
    private readonly List<FileSystemWatcher> watchers = new();
    private readonly string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    private readonly string codexHome;
    private readonly string claudeHome;
    private readonly CancellationTokenSource lifetime = new();
    private readonly CancellationToken cancellation;
    private int activity;
    private bool fetching;
    private DateTimeOffset nextRequest;
    private DateTimeOffset lastRequest;
    private string? identity;
    private string? claudeCache;
    public bool Claude { get; }
    public Snapshot? Data { get; private set; }
    public string Status { get; private set; } = "연결 중";
    public bool Failed { get; private set; }
    internal bool HasActivity => Volatile.Read(ref activity) != 0;
    public event Action? Changed;

    public UsageSource(bool claude)
    {
        Claude = claude;
        cancellation = lifetime.Token;
        codexHome = Environment.GetEnvironmentVariable("CODEX_HOME") ?? Path.Combine(home, ".codex");
        claudeHome = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR") ?? Path.Combine(home, ".claude");
        Watch(Path.Combine(claude ? claudeHome : codexHome, claude ? "projects" : "sessions"), "*.jsonl", true);
        Watch(claude ? claudeHome : codexHome, claude ? ".credentials.json" : "auth.json", false);
        if (claude) Watch(Path.Combine(home, ".cache", "claude-dashboard"), "cache-*.json", false);
    }

    private void Watch(string directory, string filter, bool recursive)
    {
        if (!Directory.Exists(directory)) return;
        var watcher = new FileSystemWatcher(directory, filter)
        {
            IncludeSubdirectories = recursive,
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size
        };
        watcher.Changed += (_, _) => Interlocked.Exchange(ref activity, 1);
        watcher.Created += (_, _) => Interlocked.Exchange(ref activity, 1);
        watcher.Renamed += (_, _) => Interlocked.Exchange(ref activity, 1);
        watcher.Error += (_, _) => Interlocked.Exchange(ref activity, 1);
        watcher.EnableRaisingEvents = true;
        watchers.Add(watcher);
    }

    internal static JsonDocument ReadJson(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return JsonDocument.Parse(stream);
    }

    private void ReadClaudeCache()
    {
        if (!Claude || claudeCache is null || !File.Exists(claudeCache)) return;
        try
        {
            using var json = ReadJson(claudeCache);
            var stamp = UsageParser.Property(json.RootElement, "timestamp");
            if (stamp.ValueKind != JsonValueKind.Number || !stamp.TryGetInt64(out var milliseconds)) return;
            var date = DateTimeOffset.FromUnixTimeMilliseconds(milliseconds);
            var now = DateTimeOffset.UtcNow;
            if (date > now.AddSeconds(5) || date < now.AddMinutes(-2) || (Data is not null && date <= Data.Retrieved)) return;
            var snapshot = UsageParser.Parse(UsageParser.Property(json.RootElement, "data"), true, date);
            if (snapshot.FiveHour is null && snapshot.Weekly is null) return;
            Data = snapshot;
            Failed = false;
            Status = "자동 갱신";
            Changed?.Invoke();
        }
        catch (IOException) { }
        catch (JsonException) { }
        catch (ArgumentOutOfRangeException) { }
        catch (UnauthorizedAccessException) { }
    }

    public async Task TickAsync(bool force = false)
    {
        if (fetching || lifetime.IsCancellationRequested) return;
        ReadClaudeCache();
        var now = DateTimeOffset.UtcNow;
        // Multiple transcript writes are combined; usage endpoints are never called for every token.
        bool active = HasActivity;
        if (!force && (now < nextRequest || (!active && now - lastRequest < TimeSpan.FromSeconds(60)))) return;
        Interlocked.Exchange(ref activity, 0);
        fetching = true;
        lastRequest = now;
        nextRequest = now.AddSeconds(15);
        try
        {
            var path = Path.Combine(Claude ? claudeHome : codexHome, Claude ? ".credentials.json" : "auth.json");
            using var auth = ReadJson(path);
            var tokens = UsageParser.Property(auth.RootElement, Claude ? "claudeAiOauth" : "tokens");
            var tokenNode = UsageParser.Property(tokens, Claude ? "accessToken" : "access_token");
            string? token = tokenNode.ValueKind == JsonValueKind.String ? tokenNode.GetString() : null;
            if (string.IsNullOrWhiteSpace(token)) throw new InvalidOperationException("로그인 필요");
            var accountNode = UsageParser.Property(tokens, "account_id");
            string? account = accountNode.ValueKind == JsonValueKind.String ? accountNode.GetString() : null;
            string currentIdentity = Claude ? Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))) : account ?? "";
            if (identity is not null && identity != currentIdentity) Data = null;
            identity = currentIdentity;
            if (Claude)
            {
                claudeCache = Path.Combine(home, ".cache", "claude-dashboard", "cache-" + currentIdentity[..16].ToLowerInvariant() + ".json");
                ReadClaudeCache();
            }
            using var request = new HttpRequestMessage(HttpMethod.Get,
                Claude ? "https://api.anthropic.com/api/oauth/usage" : "https://chatgpt.com/backend-api/wham/usage");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            request.Headers.UserAgent.ParseAdd(Claude ? "claude-code/2.1.0" : "codex-cli");
            if (Claude) request.Headers.Add("anthropic-beta", "oauth-2025-04-20");
            else if (!string.IsNullOrWhiteSpace(account)) request.Headers.Add("ChatGPT-Account-Id", account);
            using var response = await Client.SendAsync(request, cancellation);
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                var retry = response.Headers.RetryAfter;
                var delay = retry?.Delta ?? (retry?.Date is { } retryDate ? retryDate - DateTimeOffset.UtcNow : TimeSpan.FromMinutes(2));
                nextRequest = DateTimeOffset.UtcNow.AddSeconds(Math.Clamp(delay.TotalSeconds, 60, 3600));
                throw new InvalidOperationException("조회 제한 · 자동 재시도");
            }
            if (response.StatusCode == HttpStatusCode.Unauthorized || response.StatusCode == HttpStatusCode.Forbidden)
            {
                nextRequest = now.AddMinutes(1);
                throw new InvalidOperationException("로그인 확인 필요");
            }
            if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"조회 실패 ({(int)response.StatusCode})");
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellation));
            var snapshot = UsageParser.Parse(json.RootElement, Claude, DateTimeOffset.UtcNow);
            if (snapshot.FiveHour is null && snapshot.Weekly is null) throw new InvalidOperationException("한도 정보 없음");
            Data = snapshot;
            Failed = false;
            Status = "자동 갱신";
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { return; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or
            InvalidOperationException or HttpRequestException or OperationCanceledException)
        {
            Failed = true;
            Status = error switch
            {
                FileNotFoundException => "로그인 필요",
                DirectoryNotFoundException => "설치·로그인 확인",
                UnauthorizedAccessException => "로그인 파일 접근 실패",
                InvalidOperationException => error.Message,
                JsonException => "데이터 확인 필요",
                _ => "연결 실패 · 자동 재시도"
            };
            if (nextRequest < now.AddSeconds(60)) nextRequest = now.AddSeconds(60);
        }
        finally { fetching = false; }
        if (!lifetime.IsCancellationRequested) Changed?.Invoke();
    }

    public void Dispose()
    {
        lifetime.Cancel();
        foreach (var watcher in watchers) watcher.Dispose();
        lifetime.Dispose();
    }
}
