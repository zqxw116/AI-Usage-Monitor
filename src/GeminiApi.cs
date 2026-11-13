using System.Globalization;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace UsageMonitor;

internal sealed record GeminiUsage(Snapshot Data, string Identity, string Detail);
internal sealed class GeminiAuthenticationException(string message) : InvalidOperationException(message);

internal static class GeminiApi
{
    private const string Endpoint = "https://cloudcode-pa.googleapis.com/v1internal:";

    internal static async Task<GeminiUsage> ReadAsync(
        Func<HttpRequestMessage, Task<JsonDocument>> send, CancellationToken token, Action<string>? onIdentity = null)
    {
        token.ThrowIfCancellationRequested();
        string accessToken;
        try
        {
            var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".gemini", "oauth_creds.json");
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            using var credentials = await JsonDocument.ParseAsync(stream, cancellationToken: token);
            accessToken = Text(credentials.RootElement, "access_token") ?? "";
            if (string.IsNullOrWhiteSpace(accessToken) || accessToken.Any(char.IsWhiteSpace))
                throw new GeminiAuthenticationException("Gemini CLI 로그인 필요");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            throw new GeminiAuthenticationException("Gemini CLI 로그인 필요");
        }

        var identity = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(accessToken)));
        onIdentity?.Invoke(identity);

        string? planLabel = null;
        var project = Environment.GetEnvironmentVariable("GOOGLE_CLOUD_PROJECT");
        if (string.IsNullOrWhiteSpace(project))
            project = Environment.GetEnvironmentVariable("GOOGLE_CLOUD_PROJECT_ID");
        if (string.IsNullOrWhiteSpace(project))
        {
            // HEALTH_CHECK only discovers an existing Code Assist project; never onboard a user.
            using var request = Request("loadCodeAssist", accessToken, new
            {
                mode = "HEALTH_CHECK",
                metadata = new { ideType = "IDE_UNSPECIFIED", ideName = "AI Usage Monitor", platform = "WINDOWS_AMD64" }
            });
            token.ThrowIfCancellationRequested();
            using var response = await send(request);
            token.ThrowIfCancellationRequested();
            project = Text(response.RootElement, "cloudaicompanionProject");
            planLabel = ParsePlan(response.RootElement);
        }
        if (string.IsNullOrWhiteSpace(project))
            throw new InvalidOperationException("Gemini Code Assist 프로젝트 확인 필요");

        using var quotaRequest = Request("retrieveUserQuota", accessToken, new { project = project.Trim() });
        token.ThrowIfCancellationRequested();
        using var quotaResponse = await send(quotaRequest);
        token.ThrowIfCancellationRequested();
        var buckets = ParseBuckets(quotaResponse.RootElement);
        var selected = new List<Bucket>();
        foreach (var family in new[] { "pro", "flash" })
        {
            var bucket = buckets.Where(item => item.Model.Split('-').Contains(family, StringComparer.OrdinalIgnoreCase))
                .OrderBy(item => item.Fraction).ThenBy(item => item.Model, StringComparer.Ordinal).FirstOrDefault();
            if (bucket is not null) selected.Add(bucket);
        }
        if (selected.Count == 0)
            selected.AddRange(buckets.OrderBy(item => item.Fraction).ThenBy(item => item.Model, StringComparer.Ordinal)
                .GroupBy(item => item.Model, StringComparer.Ordinal).Select(group => group.First()).Take(2));
        if (selected.Count == 0) throw new InvalidOperationException("Gemini 모델 한도 미제공");

        var first = selected[0];
        var second = selected.Count > 1 ? selected[1] : null;
        var snapshot = new Snapshot(ToQuota(first), second is null ? null : ToQuota(second), DateTimeOffset.UtcNow,
            Label(first.Model, 1), second is null ? "미제공" : Label(second.Model, 2), PlanLabel: planLabel);
        var detail = "실험 · CLI/Code Assist · 남은 비율 최소 버킷 보수 표시 · " +
            string.Join(" / ", selected.Select(item => item.Model + " (" + item.Kind + ")"));
        return new GeminiUsage(snapshot, identity, detail);
    }

    internal static string? ParsePlan(JsonElement root)
    {
        var paid = UsageParser.Property(root, "paidTier");
        var tier = paid.ValueKind == JsonValueKind.Object ? paid : UsageParser.Property(root, "currentTier");
        var name = Text(tier, "name");
        if (!string.IsNullOrWhiteSpace(name) && name.Length <= 80 && !name.Any(char.IsControl)) return name.Trim();
        return Text(tier, "id") switch
        {
            "free-tier" => "Free",
            "legacy-tier" => "Legacy",
            "standard-tier" => "Standard",
            _ => null
        };
    }

    private static HttpRequestMessage Request(string method, string accessToken, object body)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, Endpoint + method);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        return request;
    }

    private sealed record Bucket(string Model, string Kind, double Fraction, DateTimeOffset? Reset);

    private static List<Bucket> ParseBuckets(JsonElement root)
    {
        var result = new List<Bucket>();
        var buckets = UsageParser.Property(root, "buckets");
        if (buckets.ValueKind != JsonValueKind.Array) return result;
        foreach (var bucket in buckets.EnumerateArray())
        {
            var model = Text(bucket, "modelId");
            var fraction = UsageParser.Property(bucket, "remainingFraction");
            if (string.IsNullOrWhiteSpace(model) || model.Length > 100 ||
                !model.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.') ||
                fraction.ValueKind != JsonValueKind.Number || !fraction.TryGetDouble(out var remaining) ||
                !double.IsFinite(remaining) || remaining < 0 || remaining > 1) continue;
            var kind = Text(bucket, "tokenType");
            if (string.IsNullOrWhiteSpace(kind) || kind.Length > 40 ||
                !kind.All(character => char.IsAsciiLetterOrDigit(character) || character == '_')) kind = "종류 미제공";
            DateTimeOffset? reset = null;
            if (DateTimeOffset.TryParse(Text(bucket, "resetTime"), CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal, out var date)) reset = date;
            result.Add(new Bucket(model, kind, remaining, reset));
        }
        return result;
    }

    private static Quota ToQuota(Bucket bucket) => new((1 - bucket.Fraction) * 100, bucket.Reset);

    private static string Label(string model, int index)
    {
        if (model.Split('-').Contains("pro", StringComparer.OrdinalIgnoreCase)) return "Pro";
        if (model.Split('-').Contains("flash", StringComparer.OrdinalIgnoreCase)) return "Flash";
        return "모델" + index;
    }

    private static string? Text(JsonElement element, string property)
    {
        var value = UsageParser.Property(element, property);
        return value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    }
}
