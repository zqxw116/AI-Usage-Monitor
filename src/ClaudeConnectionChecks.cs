using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace UsageMonitor;

internal static class ClaudeConnectionChecks
{
    private sealed class Handler : HttpMessageHandler
    {
        public Func<HttpRequestMessage, HttpResponseMessage> Reply = _ => Json();
        public int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellation)
        {
            Calls++;
            return Task.FromResult(Reply(request));
        }
    }

    private static HttpResponseMessage Json(string body = "{\"five_hour\":{\"utilization\":35,\"resets_at\":null},\"seven_day\":{\"utilization\":65,\"resets_at\":null}}") =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    public static void Run(string directory, Action<bool, string> assert)
    {
        var empty = Path.Combine(directory, "앱 연결 합성 검증");
        Directory.CreateDirectory(empty);
        File.Delete(Path.Combine(empty, ".credentials.json"));
        const string organization = "b7ee2e9a-2e56-4859-90f2-758a86b47701";
        CheckEncryption(assert, organization);
        var handler = new Handler();
        using var client = new HttpClient(handler);
        ClaudeDesktopCredential? credential = new() { Bearer = "synthetic-desktop-token" };
        bool bearerCorrect = false;
        handler.Reply = request =>
        {
            bearerCorrect = request.RequestUri!.Host == "api.anthropic.com" && request.Headers.Authorization?.Parameter == "synthetic-desktop-token";
            return Json();
        };
        using (var source = new UsageSource(true, client, empty, () => credential))
        {
            source.TickAsync(true).GetAwaiter().GetResult();
            assert(bearerCorrect && !source.Failed && source.Data?.FiveHour?.Remaining == 65 && source.Status.EndsWith("앱"), "Code 로그인 파일 없이 Desktop OAuth로 사용량 조회");
            credential = null;
            source.TickAsync(true).GetAwaiter().GetResult();
            assert(source.Failed && source.Data is null, "앱 로그아웃 후 이전 사용량 제거");
            credential = new() { Bearer = "synthetic-desktop-token" };
            source.TickAsync(true).GetAwaiter().GetResult();
            assert(!source.Failed && source.Data is not null, "앱 재로그인 후 재시작 없이 연결 복구");
        }
        credential = new() { SessionKey = "synthetic-session", Organization = organization };
        bool cookieCorrect = false;
        handler.Reply = request =>
        {
            if (request.RequestUri!.AbsolutePath == "/api/organizations")
                return Json("[{\"uuid\":\"" + organization + "\",\"subscription_type\":\"team\"}]");
            cookieCorrect = request.RequestUri!.AbsoluteUri == "https://claude.ai/api/organizations/" + organization + "/usage" &&
                request.Headers.GetValues("Cookie").Single() == "sessionKey=synthetic-session" && request.Headers.Authorization is null;
            return Json();
        };
        using (var source = new UsageSource(true, client, empty, () => credential))
        {
            source.TickAsync(true).GetAwaiter().GetResult();
            assert(cookieCorrect && !source.Failed && source.Data?.Weekly?.Remaining == 35, "OAuth 캐시 없는 Chat 앱 로그인은 세션 쿠키로 조회");
        }
        credential = new() { SessionKey = "synthetic-session" };
        int organizationCalls = 0;
        handler.Reply = request =>
        {
            if (request.RequestUri!.AbsolutePath == "/api/organizations")
            {
                organizationCalls++;
                return Json("[{\"uuid\":\"" + organization + "\"}]");
            }
            return Json();
        };
        using (var source = new UsageSource(true, client, empty, () => credential))
        {
            source.TickAsync(true).GetAwaiter().GetResult();
            assert(organizationCalls == 1 && !source.Failed, "활성 조직 쿠키가 없으면 단일 조직만 안전하게 선택");
        }
        handler.Reply = _ => Json("[{\"uuid\":\"" + organization + "\"},{\"uuid\":\"other\"}]");
        using (var source = new UsageSource(true, client, empty, () => credential))
        {
            source.TickAsync(true).GetAwaiter().GetResult();
            assert(source.Failed && source.Data is null && source.Status.Contains("조직 선택"), "여러 조직에서 임의 조직 사용량을 표시하지 않음");
        }
        File.WriteAllText(Path.Combine(empty, ".credentials.json"), "{\"claudeAiOauth\":{\"accessToken\":\"synthetic-code-token\"}}");
        credential = new() { Bearer = "synthetic-desktop-token" };
        handler.Reply = request => request.Headers.Authorization?.Parameter == "synthetic-code-token" ?
            new(HttpStatusCode.Unauthorized) : Json();
        int before = handler.Calls;
        using (var source = new UsageSource(true, client, empty, () => credential))
        {
            source.TickAsync(true).GetAwaiter().GetResult();
            assert(handler.Calls == before + 1 && !source.Failed && source.Status.EndsWith("앱"), "유효한 Desktop 로그인으로 만료된 Code 인증 조회를 생략");
        }
        handler.Reply = _ => Json();
        bool desktopRead = false;
        using (var source = new UsageSource(true, client, empty, () => { desktopRead = true; return null; }))
        {
            source.TickAsync(true).GetAwaiter().GetResult();
            assert(desktopRead && !source.Failed && source.Status.EndsWith("Code"), "앱 인증이 없으면 기존 Code 인증으로 연결");
        }
        using (var source = new UsageSource(true, client, empty, () => null))
        {
            source.TickAsync(true).GetAwaiter().GetResult();
            var previous = source.Data;
            handler.Reply = _ =>
            {
                var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
                response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromMinutes(3));
                return response;
            };
            source.TickAsync(true).GetAwaiter().GetResult();
            int calls = handler.Calls;
            source.TickAsync().GetAwaiter().GetResult();
            assert(previous is not null && source.Failed && source.Data == previous && handler.Calls == calls,
                "정상 조회 후 429가 발생해도 마지막 사용량·조회 시각 유지 및 재시도 대기");
            handler.Reply = _ => new(HttpStatusCode.ServiceUnavailable);
            source.TickAsync(true).GetAwaiter().GetResult();
            assert(source.Failed && source.Data == previous, "서버 일시 장애에도 마지막 사용량 보존");
            handler.Reply = _ => throw new HttpRequestException("synthetic network failure");
            source.TickAsync(true).GetAwaiter().GetResult();
            assert(source.Failed && source.Data == previous, "네트워크 일시 장애에도 마지막 사용량 보존");
            File.WriteAllText(Path.Combine(empty, ".credentials.json"), "{\"claudeAiOauth\":{\"accessToken\":\"synthetic-other-account-token\"}}");
            source.TickAsync(true).GetAwaiter().GetResult();
            assert(source.Failed && source.Data is null, "인증 대상이 바뀌면 이전 계정 사용량은 보존하지 않음");
        }
        File.WriteAllText(Path.Combine(empty, ".credentials.json"), "{\"claudeAiOauth\":{\"accessToken\":\"synthetic-code-token\",\"expiresAt\":1}}");
        handler.Reply = _ => Json();
        desktopRead = false;
        using (var source = new UsageSource(true, client, empty, () => { desktopRead = true; return null; }))
        {
            source.TickAsync(true).GetAwaiter().GetResult();
            assert(desktopRead && !source.Failed && source.Status.EndsWith("Code"),
                "과거 만료 메타데이터라도 서버가 수락하는 CLI 인증은 사용");
        }
        handler.Reply = _ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromMinutes(3));
            return response;
        };
        before = handler.Calls;
        using (var source = new UsageSource(true, client, empty, () => null))
        {
            source.TickAsync(true).GetAwaiter().GetResult();
            source.TickAsync().GetAwaiter().GetResult();
            assert(handler.Calls == before + 1 && source.Failed && source.Status.Contains("조회 제한"), "Code 429 재시도 시간을 지키고 즉시 반복 요청하지 않음");
        }
        File.Delete(Path.Combine(empty, ".credentials.json"));
        credential = new() { Bearer = "synthetic-desktop-token", SessionKey = "synthetic-session", Organization = organization };
        handler.Reply = request => request.Headers.Authorization is not null ? new(HttpStatusCode.Unauthorized) : Json();
        using (var source = new UsageSource(true, client, empty, () => credential))
        {
            source.TickAsync(true).GetAwaiter().GetResult();
            assert(!source.Failed && source.Data is not null, "만료된 Desktop OAuth는 유효한 앱 세션으로 연결");
            handler.Reply = _ => new(HttpStatusCode.Forbidden);
            source.TickAsync(true).GetAwaiter().GetResult();
            assert(source.Failed && source.Data is null, "서버가 로그인을 거부하면 남은 사용량을 표시하지 않음");
        }
        var now = DateTimeOffset.UtcNow;
        assert(UsageSource.RequestDue(true, false, now, now.AddSeconds(-1), now.AddSeconds(-15)), "앱 사용은 예약 시각이 되면 CLI 활동 파일 없이 조회");
        assert(!UsageSource.RequestDue(true, true, now, now.AddSeconds(30), now.AddMinutes(-1)), "활동 중에도 서버 재시도 대기 시간을 준수");
        assert(!UsageSource.RequestDue(false, false, now, now.AddSeconds(-1), now.AddSeconds(-15)), "Codex 비활동 조회 간격은 기존대로 유지");
        assert(UsageSource.RequestDue(false, true, now, now.AddSeconds(-1), now.AddSeconds(-15)), "CLI 활동 중에는 15초 간격 조회 허용");
        assert(UsageSource.RequestDue(false, false, now, now.AddSeconds(-1), now.AddSeconds(-60)), "CLI 비활동 중에는 60초 간격 조회 허용");
    }

    private static void CheckEncryption(Action<bool, string> assert, string organization)
    {
        const string account = "b91a030a-82bb-4484-940b-86e2d945e905";
        string prefix = "acct:" + account + "|9d1c250a-e61b-44d9-88ed-5944d1962f5e:" + organization + ":https://api.anthropic.com:";
        const string scope = "user:inference user:file_upload user:profile";
        var now = DateTimeOffset.UtcNow;
        var entries = new Dictionary<string, object?>
        {
            [prefix + scope] = new { token = "synthetic-base", expiresAt = now.AddHours(1).ToUnixTimeMilliseconds() },
            [prefix + scope + " user:sessions:claude_code"] = new { token = "synthetic-code", expiresAt = now.AddHours(1).ToUnixTimeMilliseconds() }
        };
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(entries));
        assert(ClaudeDesktopAuth.ParseTokenCache(json.RootElement, account, organization, now) == "synthetic-base", "Desktop 캐시에서 활성 계정·조직의 기본 범위 우선 선택");
        assert(ClaudeDesktopAuth.ParseTokenCache(json.RootElement, Guid.NewGuid().ToString(), organization, now) is null, "다른 계정 OAuth 캐시 거부");
        assert(ClaudeDesktopAuth.ParseTokenCache(json.RootElement, account, Guid.NewGuid().ToString(), now) is null, "다른 조직 OAuth 캐시 거부");
        assert(ClaudeDesktopAuth.ParseTokenCache(json.RootElement, account, organization, now.AddHours(2)) is null, "만료된 앱 토큰 거부");
        entries[prefix + scope] = null;
        using var tombstone = JsonDocument.Parse(JsonSerializer.Serialize(entries));
        assert(ClaudeDesktopAuth.ParseTokenCache(tombstone.RootElement, account, organization, now) == "synthetic-code", "삭제 표시된 토큰을 사용하지 않고 유효 범위 선택");
        byte[] key = RandomNumberGenerator.GetBytes(32);
        byte[] nonce = RandomNumberGenerator.GetBytes(12);
        byte[] value = Encoding.UTF8.GetBytes("synthetic-session");
        byte[] bound = SHA256.HashData(Encoding.UTF8.GetBytes(".claude.ai")).Concat(value).ToArray();
        byte[] cipher = new byte[bound.Length], tag = new byte[16];
        using var aes = new AesGcm(key, 16);
        aes.Encrypt(nonce, bound, cipher, tag);
        byte[] blob = "v10"u8.ToArray().Concat(nonce).Concat(cipher).Concat(tag).ToArray();
        assert(ClaudeDesktopAuth.DecryptV10(blob, key, ".claude.ai", 24).SequenceEqual(value), "v24 쿠키 AES-GCM 복호화와 호스트 접두부 제거");
        bool Rejects(byte[] input, string host)
        {
            try { ClaudeDesktopAuth.DecryptV10(input, key, host, 24); return false; }
            catch (InvalidOperationException) { return true; }
        }
        assert(Rejects(blob, "other.example"), "다른 도메인의 쿠키 암호문 거부");
        blob[^1] ^= 1;
        assert(Rejects(blob, ".claude.ai"), "인증 태그가 변조된 암호문 거부");
        blob[1] = (byte)'2';
        assert(Rejects(blob, ".claude.ai"), "지원하지 않는 v20 암호화 우회 없이 거부");
        CryptographicOperations.ZeroMemory(key);
    }
}
