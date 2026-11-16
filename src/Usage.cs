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

internal enum ServiceKind { Codex, Claude, Gemini }

internal sealed record Snapshot(Quota? FiveHour, Quota? Weekly, DateTimeOffset Retrieved,
    string? PrimaryLabel = null, string? SecondaryLabel = null, bool CodexPro = false, string? PlanLabel = null);

internal static class UsageParser
{
    internal static string? ClaudeResponsePlan(JsonElement response)
    {
        foreach (var key in new[] { "subscription_type", "plan_type" })
        {
            var plan = Property(response, key);
            if (plan.ValueKind != JsonValueKind.String) continue;
            string? label = plan.GetString()?.ToLowerInvariant() switch
            {
                "free" => "Free",
                "pro" => "Pro",
                "max" => "Max",
                "team" => "Team",
                "enterprise" => "Enterprise",
                _ => null
            };
            if (label is not null) return label;
        }
        return null;
    }

    internal static string? ClaudePlan(JsonElement metadata)
    {
        var subscription = Property(metadata, "subscriptionType");
        if (subscription.ValueKind != JsonValueKind.String) return null;
        var tier = Property(metadata, "rateLimitTier");
        return subscription.GetString()?.ToLowerInvariant() switch
        {
            "free" => "Free",
            "pro" => "Pro",
            "max" => tier.ValueKind == JsonValueKind.String ? tier.GetString() switch
            {
                "default_claude_max_5x" => "Max 5x",
                "default_claude_max_20x" => "Max 20x",
                _ => "Max"
            } : "Max",
            "team" => "Team",
            "enterprise" => "Enterprise",
            _ => null
        };
    }

    internal static string? ClaudeOrganizationPlan(JsonElement organization)
    {
        string? plan = ClaudeResponsePlan(organization);
        if (plan is not null) return plan;
        var type = Property(organization, "organization_type");
        if (type.ValueKind != JsonValueKind.String) return null;
        return type.GetString() switch
        {
            "claude_pro" => "Pro",
            "claude_max" => "Max",
            "claude_team" => "Team",
            "claude_enterprise" => "Enterprise",
            _ => null
        };
    }

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
            ParseWindow(Property(root, "seven_day"), "utilization", "resets_at", false), timestamp,
            PlanLabel: ClaudeResponsePlan(root));
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
        var plan = Property(root, "plan_type");
        string? planType = plan.ValueKind == JsonValueKind.String ? plan.GetString()?.ToLowerInvariant() : null;
        string? planLabel = planType switch
        {
            "free" => "Free",
            "go" => "Go",
            "plus" => "Plus",
            "pro" => "Pro",
            "prolite" => "Pro Lite",
            "promax" => "Pro Max",
            "team" => "Team",
            "business" => "Business",
            "enterprise" => "Enterprise",
            "edu" => "Edu",
            _ => null
        };
        return new Snapshot(five, week, timestamp, CodexPro: planType is "pro" or "prolite" or "promax",
            PlanLabel: planLabel);
    }
}

internal sealed class UsageSource : IDisposable
{
    private static readonly HttpClient Client = new(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false })
        { Timeout = TimeSpan.FromSeconds(15) };
    private readonly HttpClient http;
    private readonly Func<ClaudeDesktopCredential?> desktopAuth;
    private readonly bool desktopOnly;
    private readonly Func<string, CancellationToken, Task> renewClaude;
    private readonly List<FileSystemWatcher> watchers = new();
    private readonly string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    private readonly string codexHome;
    private readonly string claudeHome;
    private readonly CancellationTokenSource lifetime = new();
    private CancellationToken cancellation;
    private CancellationTokenSource? currentRequest;
    private int activity;
    private bool fetching;
    private TaskCompletionSource? fetchFinished;
    private Task? refreshTask;
    public bool Refreshing { get; private set; }
    private bool desktopConnection;
    private DateTimeOffset nextRequest;
    private DateTimeOffset rateLimitedUntil;
    private DateTimeOffset lastRequest;
    private string? identity;
    private string? claudeCache;
    private string? claudePlanLabel;
    private string? claudePlanDetail;
    private bool claudePlanChecked;
    public ServiceKind Kind { get; }
    public bool Claude => Kind == ServiceKind.Claude;
    public bool Gemini => Kind == ServiceKind.Gemini;
    public string Name => Kind.ToString();
    public string? Detail { get; private set; }
    public bool Enabled { get; private set; } = true;
    public bool Connecting { get; private set; }
    public Snapshot? Data { get; private set; }
    public string Status { get; private set; } = "연결 중";
    public bool Failed { get; private set; }
    internal bool HasActivity => Volatile.Read(ref activity) != 0;
    public event Action? Changed;

    internal void SetEnabled(bool enabled)
    {
        if (Enabled == enabled) return;
        Enabled = enabled;
        currentRequest?.Cancel();
        Data = null;
        Detail = null;
        claudeCache = null;
        identity = null;
        Failed = false;
        Status = enabled ? "연결 중" : "비활성";
        Reconnect();
        Changed?.Invoke();
    }

    internal void Reconnect()
    {
        currentRequest?.Cancel();
        nextRequest = default;
        lastRequest = default;
        Interlocked.Exchange(ref activity, 1);
    }

    internal Task RefreshAsync()
    {
        if (Refreshing) return refreshTask ?? Task.CompletedTask;
        if (!Enabled || Connecting || lifetime.IsCancellationRequested) return Task.CompletedTask;
        Refreshing = true;
        refreshTask = RefreshCoreAsync();
        return refreshTask;
    }

    private async Task RefreshCoreAsync()
    {
        // Yield before notifying controls so all callers share the same in-flight task.
        await Task.Yield();
        try
        {
            if (!lifetime.IsCancellationRequested) Changed?.Invoke();
            if (fetchFinished is { } pending) await pending.Task;
            if (!Enabled || Connecting || lifetime.IsCancellationRequested) return;
            Status = "로그인·사용량 확인 중";
            Changed?.Invoke();
            if (Claude && claudePlanLabel is null) claudePlanChecked = false;
            await TickAsync(true);
            if (!Failed && Data is not null) Status = "연결 확인됨 · 방금 갱신";
        }
        finally
        {
            Refreshing = false;
            if (!lifetime.IsCancellationRequested) Changed?.Invoke();
        }
    }

    internal void SetConnecting(bool connecting)
    {
        if (Connecting == connecting) return;
        Connecting = connecting;
        Reconnect();
        if (connecting)
        {
            Data = null;
            Detail = null;
            claudeCache = null;
            identity = null;
        }
        Failed = false;
        Status = !Enabled ? "비활성" : connecting ? "브라우저 로그인 대기 중" : "연결 중";
        Changed?.Invoke();
    }

    public UsageSource(bool claude, HttpClient? httpClient = null, string? claudeDirectory = null,
        Func<ClaudeDesktopCredential?>? desktopReader = null, bool desktopOnly = false,
        Func<string, CancellationToken, Task>? claudeRenewal = null)
        : this(claude ? ServiceKind.Claude : ServiceKind.Codex, httpClient, claudeDirectory, desktopReader, desktopOnly, claudeRenewal) { }

    public UsageSource(ServiceKind kind, HttpClient? httpClient = null, string? claudeDirectory = null,
        Func<ClaudeDesktopCredential?>? desktopReader = null, bool desktopOnly = false,
        Func<string, CancellationToken, Task>? claudeRenewal = null)
    {
        Kind = kind;
        http = httpClient ?? Client;
        desktopAuth = desktopReader ?? ClaudeDesktopAuth.Read;
        this.desktopOnly = desktopOnly;
        renewClaude = claudeRenewal ?? ClaudeAuthRenewal.RenewAsync;
        cancellation = lifetime.Token;
        codexHome = Environment.GetEnvironmentVariable("CODEX_HOME") ?? Path.Combine(home, ".codex");
        claudeHome = claudeDirectory ?? Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR") ?? Path.Combine(home, ".claude");
        if (Gemini)
        {
            Watch(Path.Combine(home, ".gemini"), "oauth_creds.json", false);
        }
        else
        {
            Watch(Path.Combine(Claude ? claudeHome : codexHome, Claude ? "projects" : "sessions"), "*.jsonl", true);
            Watch(Claude ? claudeHome : codexHome, Claude ? ".credentials.json" : "auth.json", false);
            if (Claude) Watch(Path.Combine(home, ".cache", "claude-dashboard"), "cache-*.json", false);
        }
    }

    internal static bool RequestDue(bool desktop, bool active, DateTimeOffset now, DateTimeOffset next, DateTimeOffset last) =>
        now >= next && (desktop || active || now - last >= TimeSpan.FromSeconds(60));

    private sealed class LoginException : InvalidOperationException
    {
        public LoginException(string message) : base(message) { }
    }

    private sealed class AccessException(string message) : InvalidOperationException(message) { }
    private sealed class RateLimitException(string message) : InvalidOperationException(message) { }

    private async Task<JsonDocument> SendAsync(HttpRequestMessage request)
    {
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.UserAgent.ParseAdd(Kind == ServiceKind.Codex ? "codex-cli" : "AIUsageMonitor/1.0.0");
        using var response = await http.SendAsync(request, cancellation);
        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            var retry = response.Headers.RetryAfter;
            var delay = retry?.Delta ?? (retry?.Date is { } date ? date - DateTimeOffset.UtcNow : TimeSpan.FromMinutes(2));
            nextRequest = DateTimeOffset.UtcNow.AddSeconds(Math.Clamp(delay.TotalSeconds, 60, 3600));
            rateLimitedUntil = nextRequest;
            throw new RateLimitException(Claude ? $"조회 제한 · {nextRequest.ToLocalTime():HH:mm:ss} 재시도" : "조회 제한 · 자동 재시도");
        }
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            if (Gemini && response.StatusCode == HttpStatusCode.Forbidden)
                throw new LoginException("Gemini API 접근 미지원 (403)");
            if (Claude && response.StatusCode == HttpStatusCode.Forbidden)
            {
                nextRequest = DateTimeOffset.UtcNow.AddMinutes(1);
                throw new AccessException(request.RequestUri?.Host == "claude.ai" ?
                    "Claude 앱 사용량 접근 제한 (403) · 자동 재시도" : "Claude 사용량 접근 제한 (403) · 자동 재시도");
            }
            throw new LoginException(request.RequestUri?.Host == "claude.ai" ?
                "Claude 앱 로그인 확인 필요" : Name + " 로그인 확인 필요");
        }
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"조회 실패 ({(int)response.StatusCode})");
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellation));
    }

    private async Task ReadClaudeAsync()
    {
        InvalidOperationException? desktopFailure = null;
        ClaudeDesktopCredential? desktop = null;
        try { desktop = await Task.Run(desktopAuth, cancellation); }
        catch (InvalidOperationException error) { desktopFailure = error; }
        cancellation.ThrowIfCancellationRequested();
        if (desktop is not null)
        {
            desktopConnection = true;
            try
            {
                await ReadClaudeDesktopAsync(desktop);
                return;
            }
            catch (LoginException error) { Data = null; claudeCache = null; desktopFailure = error; }
            catch (AccessException error) { desktopFailure = error; }
            // Rate limits and transient failures retain this authentication source and its backoff.
        }
        if (desktopOnly)
        {
            if (desktopFailure is not null) throw desktopFailure;
            throw new LoginException("Claude 앱 로그인 필요");
        }
        string? token = null;
        string? planLabel = null;
        bool codeReadFailed = false;
        string? renewalNotice = null;
        bool expired = false, canRenew = false, expiryKnown = false;
        void ReadCodeLogin()
        {
            using var auth = ReadJson(Path.Combine(claudeHome, ".credentials.json"));
            var tokens = UsageParser.Property(auth.RootElement, "claudeAiOauth");
            planLabel = UsageParser.ClaudePlan(tokens);
            var node = UsageParser.Property(tokens, "accessToken");
            token = node.ValueKind == JsonValueKind.String ? node.GetString() : null;
            var expiry = UsageParser.Property(tokens, "expiresAt");
            expiryKnown = expiry.ValueKind == JsonValueKind.Number && expiry.TryGetInt64(out _);
            expired = expiryKnown && expiry.GetInt64() <= DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var refresh = UsageParser.Property(tokens, "refreshToken");
            canRenew = refresh.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(refresh.GetString());
        }
        try
        {
            ReadCodeLogin();
            if (expired && canRenew)
            {
                nextRequest = DateTimeOffset.UtcNow.AddMinutes(1);
                try { await renewClaude(claudeHome, cancellation); }
                catch (InvalidOperationException error) { renewalNotice = error.Message; }
                ReadCodeLogin();
            }
            // Local expiry metadata can lag behind the official client's login state.
            // Only the usage server can confirm whether this access token is accepted.
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        { codeReadFailed = error is not FileNotFoundException and not DirectoryNotFoundException; }
        if (!string.IsNullOrWhiteSpace(token))
        {
            desktopConnection = false;
            try
            {
                await ReadClaudeBearerAsync(token, "자동 갱신 · Code", planLabel);
                return;
            }
            catch (LoginException)
            {
                desktopConnection = true;
                Data = null;
                claudeCache = null;
                if (desktopFailure is not null && desktopFailure is not LoginException) throw desktopFailure;
                throw new LoginException(renewalNotice ?? (expired ? "Claude Code 인증 만료 · 브라우저 로그인 필요" :
                    "Claude Code 인증 확인 필요 · 브라우저 로그인"));
            }
        }
        desktopConnection = true;
        if (desktopFailure is not null) throw desktopFailure;
        if (codeReadFailed) throw new InvalidOperationException("Claude Code 인증 파일 읽기 실패 · 자동 재시도");
        throw new LoginException("Claude Code 로그인 필요 · 앱 로그인과 별도");
    }

    private async Task ReadClaudeDesktopAsync(ClaudeDesktopCredential desktop)
    {
        if (!string.IsNullOrWhiteSpace(desktop.Bearer))
        {
            try
            {
                await ReadClaudeBearerAsync(desktop.Bearer, "자동 갱신 · 앱", organization: desktop.Organization);
                return;
            }
            catch (LoginException) when (!string.IsNullOrWhiteSpace(desktop.SessionKey)) { Data = null; }
            catch (AccessException) when (!string.IsNullOrWhiteSpace(desktop.SessionKey)) { }
        }
        if (string.IsNullOrWhiteSpace(desktop.SessionKey)) throw new LoginException("Claude 앱 로그인 확인 필요");
        var organization = desktop.Organization;
        string? planLabel = null;
        bool organizationRead = false;
        HttpRequestMessage WebRequest(string url)
        {
            var request = new HttpRequestMessage(HttpMethod.Get, "https://claude.ai" + url);
            request.Headers.Add("Cookie", "sessionKey=" + desktop.SessionKey);
            return request;
        }
        if (string.IsNullOrWhiteSpace(organization))
        {
            using var request = WebRequest("/api/organizations");
            using var json = await SendAsync(request);
            if (json.RootElement.ValueKind != JsonValueKind.Array || json.RootElement.GetArrayLength() != 1)
                throw new LoginException("Claude 앱에서 사용할 조직 선택 필요");
            var id = UsageParser.Property(json.RootElement[0], "uuid");
            if (id.ValueKind == JsonValueKind.String) organization = id.GetString();
            planLabel = UsageParser.ClaudeOrganizationPlan(json.RootElement[0]);
            organizationRead = true;
        }
        if (!Guid.TryParse(organization, out _)) throw new LoginException("Claude 앱 조직 확인 필요");
        SetIdentity(desktop.SessionKey + ":" + organization);
        claudeCache = null;
        using var usageRequest = WebRequest("/api/organizations/" + organization + "/usage");
        using var usage = await SendAsync(usageRequest);
        SetClaudeData(usage.RootElement, "자동 갱신 · 앱", 60, planLabel);
        if (!claudePlanChecked)
        {
            if (organizationRead)
            {
                claudePlanChecked = true;
                Detail = claudePlanDetail = "Claude 요금제 정보 없음";
            }
            else
            {
                using var request = WebRequest("/api/organizations");
                await ReadClaudePlanAsync(request, root =>
                {
                    if (root.ValueKind != JsonValueKind.Array) return null;
                    var matches = root.EnumerateArray().Where(item =>
                        UsageParser.Property(item, "uuid") is { ValueKind: JsonValueKind.String } id &&
                        string.Equals(id.GetString(), organization, StringComparison.OrdinalIgnoreCase)).ToArray();
                    return matches.Length == 1 ? UsageParser.ClaudeOrganizationPlan(matches[0]) : null;
                });
            }
        }
    }

    private void SetIdentity(string token)
    {
        string current = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
        if (identity != current)
        {
            Data = null;
            claudePlanLabel = null;
            claudePlanDetail = null;
            claudePlanChecked = false;
        }
        identity = current;
    }

    private async Task ReadClaudeBearerAsync(string token, string status, string? planLabel = null, string? organization = null)
    {
        SetIdentity(token + (organization is null ? "" : ":" + organization));
        claudeCache = status.EndsWith("Code") ? Path.Combine(home, ".cache", "claude-dashboard", "cache-" + identity![..16].ToLowerInvariant() + ".json") : null;
        ReadClaudeCache();
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.anthropic.com/api/oauth/usage");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Add("anthropic-beta", "oauth-2025-04-20");
        using var json = await SendAsync(request);
        SetClaudeData(json.RootElement, status, planLabel: planLabel);
        if (status.EndsWith("앱") && !claudePlanChecked)
        {
            if (!Guid.TryParse(organization, out _))
            {
                claudePlanChecked = true;
                Detail = claudePlanDetail = "Claude 요금제 조직 확인 필요";
                return;
            }
            using var profile = new HttpRequestMessage(HttpMethod.Get, "https://api.anthropic.com/api/oauth/profile");
            profile.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            profile.Headers.Add("anthropic-beta", "oauth-2025-04-20");
            await ReadClaudePlanAsync(profile, root =>
            {
                var org = UsageParser.Property(root, "organization");
                var id = UsageParser.Property(org, "uuid");
                return id.ValueKind == JsonValueKind.String && string.Equals(id.GetString(), organization, StringComparison.OrdinalIgnoreCase)
                    ? UsageParser.ClaudeOrganizationPlan(org) : null;
            });
        }
    }

    private async Task ReadClaudePlanAsync(HttpRequestMessage request, Func<JsonElement, string?> parse)
    {
        string? plan = null;
        string detail = "Claude 요금제 정보 확인 불가";
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            request.Headers.UserAgent.ParseAdd("AIUsageMonitor/1.0.0");
            using var response = await http.SendAsync(request, timeout.Token);
            if (response.IsSuccessStatusCode)
            {
                using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(timeout.Token));
                plan = parse(json.RootElement);
            }
            else detail = $"Claude 요금제 조회 실패 ({(int)response.StatusCode}) · 사용량은 정상";
        }
        catch (Exception error) when (error is HttpRequestException or IOException or JsonException or OperationCanceledException)
        {
            cancellation.ThrowIfCancellationRequested();
            detail = "Claude 요금제 조회 실패 · 사용량은 정상";
        }
        cancellation.ThrowIfCancellationRequested();
        claudePlanChecked = true;
        claudePlanLabel = plan;
        Detail = claudePlanDetail = plan is null ? detail : null;
        if (Data is not null) Data = Data with { PlanLabel = plan };
    }

    private void SetClaudeData(JsonElement root, string status, int pollSeconds = 60, string? planLabel = null)
    {
        cancellation.ThrowIfCancellationRequested();
        var snapshot = UsageParser.Parse(root, true, DateTimeOffset.UtcNow);
        if (snapshot.FiveHour is null && snapshot.Weekly is null) throw new InvalidOperationException("한도 정보 없음");
        if ((snapshot.PlanLabel ?? planLabel) is { } knownPlan)
        {
            claudePlanLabel = knownPlan;
            claudePlanChecked = true;
            claudePlanDetail = null;
        }
        Data = snapshot with { PlanLabel = claudePlanLabel };
        Detail = claudePlanDetail;
        Failed = false;
        Status = status;
        nextRequest = DateTimeOffset.UtcNow.AddSeconds(pollSeconds);
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
            Data = snapshot with { PlanLabel = snapshot.PlanLabel ?? Data?.PlanLabel };
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
        if (!Enabled || Connecting || fetching || lifetime.IsCancellationRequested) return;
        if (DateTimeOffset.UtcNow < rateLimitedUntil)
        {
            if (force && Claude && !desktopConnection && identity is not null)
            {
                try
                {
                    using var auth = ReadJson(Path.Combine(claudeHome, ".credentials.json"));
                    var token = UsageParser.Property(UsageParser.Property(auth.RootElement, "claudeAiOauth"), "accessToken");
                    if (token.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(token.GetString())) SetIdentity(token.GetString()!);
                    else Data = null;
                }
                catch (FileNotFoundException) { Data = null; }
                catch (DirectoryNotFoundException) { Data = null; }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException) { }
            }
            Status = $"조회 제한 · {rateLimitedUntil.ToLocalTime():HH:mm:ss} 재시도";
            return;
        }
        ReadClaudeCache();
        var now = DateTimeOffset.UtcNow;
        // Multiple transcript writes are combined; usage endpoints are never called for every token.
        bool active = HasActivity;
        if (!force && !RequestDue(Claude && desktopConnection, active, now, nextRequest, lastRequest)) return;
        Interlocked.Exchange(ref activity, 0);
        fetching = true;
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fetchFinished = finished;
        using var requestLifetime = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        currentRequest = requestLifetime;
        cancellation = requestLifetime.Token;
        lastRequest = now;
        nextRequest = now.AddSeconds(15);
        try
        {
            if (Gemini)
            {
                var result = await GeminiApi.ReadAsync(SendAsync, cancellation, current =>
                {
                    if (identity is not null && identity != current) { Data = null; Detail = null; }
                    identity = current;
                });
                cancellation.ThrowIfCancellationRequested();
                identity = result.Identity;
                Data = result.Data;
                Detail = result.Detail;
                Failed = false;
                Status = "자동 갱신 · CLI · 실험적";
                return;
            }
            if (Claude)
            {
                await ReadClaudeAsync();
                return;
            }
            var path = Path.Combine(codexHome, "auth.json");
            using var auth = ReadJson(path);
            var tokens = UsageParser.Property(auth.RootElement, "tokens");
            var tokenNode = UsageParser.Property(tokens, "access_token");
            string? token = tokenNode.ValueKind == JsonValueKind.String ? tokenNode.GetString() : null;
            if (string.IsNullOrWhiteSpace(token)) throw new InvalidOperationException("로그인 필요");
            var accountNode = UsageParser.Property(tokens, "account_id");
            string? account = accountNode.ValueKind == JsonValueKind.String ? accountNode.GetString() : null;
            string currentIdentity = account ?? "";
            if (identity is not null && identity != currentIdentity) Data = null;
            identity = currentIdentity;
            using var request = new HttpRequestMessage(HttpMethod.Get,
                "https://chatgpt.com/backend-api/wham/usage");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            request.Headers.UserAgent.ParseAdd("codex-cli");
            if (!string.IsNullOrWhiteSpace(account)) request.Headers.Add("ChatGPT-Account-Id", account);
            using var response = await http.SendAsync(request, cancellation);
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                var retry = response.Headers.RetryAfter;
                var delay = retry?.Delta ?? (retry?.Date is { } retryDate ? retryDate - DateTimeOffset.UtcNow : TimeSpan.FromMinutes(2));
                nextRequest = DateTimeOffset.UtcNow.AddSeconds(Math.Clamp(delay.TotalSeconds, 60, 3600));
                rateLimitedUntil = nextRequest;
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
            cancellation.ThrowIfCancellationRequested();
            Data = snapshot;
            Failed = false;
            Status = "자동 갱신";
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { return; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or
            InvalidOperationException or HttpRequestException or OperationCanceledException)
        {
            if (cancellation.IsCancellationRequested) return;
            Failed = true;
            if (error is LoginException or GeminiAuthenticationException or ClaudeDesktopAuth.RenewalPendingException)
            {
                Data = null;
                Detail = null;
                claudeCache = null;
            }
            Status = error switch
            {
                FileNotFoundException => "로그인 필요",
                DirectoryNotFoundException => "설치·로그인 확인",
                UnauthorizedAccessException => "로그인 파일 접근 실패",
                InvalidOperationException => error.Message,
                JsonException => "데이터 확인 필요",
                _ => "연결 실패 · 자동 재시도"
            };
            if (error is not LoginException and not ClaudeDesktopAuth.RenewalPendingException && nextRequest < now.AddSeconds(60))
                nextRequest = now.AddSeconds(60);
        }
        finally
        {
            fetching = false;
            currentRequest = null;
            fetchFinished = null;
            finished.TrySetResult();
            if (!lifetime.IsCancellationRequested) Changed?.Invoke();
        }
    }

    public void Dispose()
    {
        lifetime.Cancel();
        foreach (var watcher in watchers) watcher.Dispose();
        lifetime.Dispose();
    }
}
