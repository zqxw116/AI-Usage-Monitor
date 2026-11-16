using System.Net;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using UsageMonitor;

internal static class Program
{
    private const string Account = "b91a030a-82bb-4484-940b-86e2d945e905";
    private const string Organization = "b7ee2e9a-2e56-4859-90f2-758a86b47701";
    private static int failed, passed;

    private static void Check(bool value, string name)
    {
        if (value) passed++; else failed++;
        Console.WriteLine((value ? "PASS " : "FAIL ") + name);
    }

    private static int Main()
    {
        string root = Path.Combine(Path.GetTempPath(), "AIUsageMonitor-auth-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            CheckDesktopPriority(root);
            ClaudeConnectionChecks.Run(Path.Combine(root, "legacy"), Check);
            string key = "acct:" + Account + "|9d1c250a-e61b-44d9-88ed-5944d1962f5e:" + Organization + ":https://api.anthropic.com:";
            var entries = new Dictionary<string, object?>
            {
                [key + "user:inference user:profile user:file_upload"] = new { token = "synthetic-sign-in", expiresAt = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeMilliseconds() }
            };
            using (var cache = JsonDocument.Parse(JsonSerializer.Serialize(entries)))
                Check(ClaudeDesktopAuth.ParseTokenCache(cache.RootElement, Account, Organization, DateTimeOffset.UtcNow) == "synthetic-sign-in",
                    "최신 Desktop의 순서가 다른 동일 OAuth 스코프 허용");
            entries[key + "user:inference user:profile user:file_upload unexpected:scope"] = entries.Values.First();
            entries.Remove(key + "user:inference user:profile user:file_upload");
            using (var cache = JsonDocument.Parse(JsonSerializer.Serialize(entries)))
                Check(ClaudeDesktopAuth.ParseTokenCache(cache.RootElement, Account, Organization, DateTimeOffset.UtcNow) is null,
                    "예상하지 않은 OAuth 권한은 계속 거부");

            string profile = Path.Combine(root, "desktop");
            WriteProfile(profile);
            Check(ReadProfile(profile)?.Bearer == "synthetic-profile-token", "쿠키 파일이 없어도 유효한 독립 앱 OAuth 인증 사용");
            using (var writer = new FileStream(Path.Combine(profile, "config.json"), FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete))
                Check(ReadProfile(profile)?.Bearer == "synthetic-profile-token", "실행 중인 앱이 설정 파일을 쓰기용으로 열어도 인증 읽기 허용");
            File.WriteAllText(Path.Combine(profile, "Cookies"), "synthetic invalid database");
            Check(ReadProfile(profile)?.Bearer == "synthetic-profile-token", "쿠키 조회 형식 오류가 유효한 앱 OAuth를 차단하지 않음");
            CheckRecovery(root, profile);
            File.WriteAllText(Path.Combine(profile, "config.json"), "{\"lastKnownAccountUuid\":\"" + Account + "\",\"oauth:tokenCacheV2\":null}");
            Check(ReadProfile(profile) is null, "로그아웃으로 제거된 OAuth 캐시를 되살리지 않음");

            string cli = Path.Combine(root, "cli");
            Directory.CreateDirectory(cli);
            using var client = new HttpClient(new ForbiddenHandler());
            using var source = new UsageSource(true, client, cli, () => new ClaudeDesktopCredential { SessionKey = "synthetic-session", Organization = Organization });
            source.TickAsync(true).GetAwaiter().GetResult();
            Check(source.Failed && source.Status.Contains("접근 제한") && !source.Status.Contains("로그인"), "403 접근 차단을 재로그인 필요로 오인하지 않음");
        }
        finally
        {
            // This directory is created above with a random name under the system temp directory.
            Directory.Delete(root, true);
        }
        Console.WriteLine($"RESULT passed={passed} failed={failed}");
        return failed == 0 ? 0 : 1;
    }

    private sealed class ForbiddenHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellation) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden));
    }

    private sealed class PriorityHandler : HttpMessageHandler
    {
        public HttpStatusCode DesktopStatus = HttpStatusCode.OK, CodeStatus = HttpStatusCode.OK;
        public int DesktopCalls, CodeCalls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellation)
        {
            bool desktop = request.Headers.Authorization?.Parameter == "synthetic-desktop-priority" || request.Headers.Contains("Cookie");
            if (desktop) DesktopCalls++; else CodeCalls++;
            var response = new HttpResponseMessage(desktop ? DesktopStatus : CodeStatus);
            if (response.StatusCode == HttpStatusCode.OK)
                response.Content = new StringContent("{\"five_hour\":{\"utilization\":29,\"resets_at\":null}}");
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
                response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(3150));
            return Task.FromResult(response);
        }
    }

    private static void CheckDesktopPriority(string root)
    {
        string cli = Path.Combine(root, "priority-cli");
        Directory.CreateDirectory(cli);
        File.WriteAllText(Path.Combine(cli, ".credentials.json"), "{\"claudeAiOauth\":{\"accessToken\":\"synthetic-code-priority\"}}");
        var handler = new PriorityHandler { CodeStatus = HttpStatusCode.TooManyRequests };
        using var client = new HttpClient(handler);
        ClaudeDesktopCredential desktop = new() { Bearer = "synthetic-desktop-priority" };
        using (var source = new UsageSource(true, client, cli, () => desktop))
        {
            source.TickAsync(true).GetAwaiter().GetResult();
            Check(!source.Failed && source.Data is not null && source.Status.EndsWith("앱") && handler.CodeCalls == 0,
                "CLI 조회 제한이 있어도 처음부터 유효한 데스크톱 인증을 우선 선택");
            Check(ScheduledAfter(source, 60), "Claude OAuth 성공 후 15초마다 다시 조회하지 않고 60초 간격 유지");
        }
        handler.DesktopStatus = HttpStatusCode.TooManyRequests;
        handler.CodeStatus = HttpStatusCode.OK;
        handler.DesktopCalls = handler.CodeCalls = 0;
        using (var source = new UsageSource(true, client, cli, () => desktop))
        {
            source.TickAsync(true).GetAwaiter().GetResult();
            source.TickAsync().GetAwaiter().GetResult();
            Check(source.Failed && source.Status.Contains("조회 제한") && handler.DesktopCalls == 1 && handler.CodeCalls == 0,
                "선택한 앱 인증이 429이면 CLI로 우회하지 않고 서버 대기를 준수");
        }
        handler.DesktopStatus = HttpStatusCode.Unauthorized;
        handler.DesktopCalls = handler.CodeCalls = 0;
        using (var source = new UsageSource(true, client, cli, () => desktop))
        {
            source.TickAsync(true).GetAwaiter().GetResult();
            Check(!source.Failed && source.Status.EndsWith("Code") && handler.DesktopCalls == 1 && handler.CodeCalls == 1,
                "앱 인증이 실제 거부되면 기존 CLI 인증으로 자동 연결");
        }
        using (var source = new UsageSource(true, client, cli, () => throw new InvalidOperationException("synthetic profile read failure")))
        {
            source.TickAsync(true).GetAwaiter().GetResult();
            Check(!source.Failed && source.Status.EndsWith("Code"), "앱 프로필을 읽지 못해도 유효한 CLI 로그인으로 연결");
        }
        handler.DesktopStatus = HttpStatusCode.OK;
        using (var source = new UsageSource(true, client, cli, () => new ClaudeDesktopCredential { SessionKey = "synthetic-session", Organization = Organization }))
        {
            source.TickAsync(true).GetAwaiter().GetResult();
            Check(!source.Failed && ScheduledAfter(source, 60), "Claude 웹 세션 성공 후에는 60초 간격으로 자동 조회");
        }
    }

    private static bool ScheduledAfter(UsageSource source, int seconds)
    {
        var next = (DateTimeOffset)typeof(UsageSource).GetField("nextRequest", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(source)!;
        double delay = (next - DateTimeOffset.UtcNow).TotalSeconds;
        return delay >= seconds - 5 && delay <= seconds + 1;
    }

    private sealed class RecoveryHandler : HttpMessageHandler
    {
        public bool CodeForbidden;
        public string? LastBearer;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellation)
        {
            LastBearer = request.Headers.Authorization?.Parameter;
            return Task.FromResult(LastBearer == "synthetic-code-expired" ? new HttpResponseMessage(CodeForbidden ? HttpStatusCode.Forbidden : HttpStatusCode.Unauthorized) :
                new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"five_hour\":{\"utilization\":29,\"resets_at\":null}}") });
        }
    }

    private static void CheckRecovery(string root, string profile)
    {
        string cli = Path.Combine(root, "expired-cli");
        Directory.CreateDirectory(cli);
        File.WriteAllText(Path.Combine(cli, ".credentials.json"), "{\"claudeAiOauth\":{\"accessToken\":\"synthetic-code-expired\"}}");
        var handler = new RecoveryHandler();
        using var client = new HttpClient(handler);
        using var source = new UsageSource(true, client, cli, () => ReadProfile(profile));
        source.TickAsync(true).GetAwaiter().GetResult();
        Check(!source.Failed && source.Data?.FiveHour?.Remaining == 71 && source.Status.EndsWith("앱"), "만료 CLI와 쿠키 장애가 함께 있어도 실제 앱 캐시 경로로 자동 연결");
        WriteProfile(profile, "synthetic-rotated-profile-token");
        source.TickAsync(true).GetAwaiter().GetResult();
        Check(!source.Failed && handler.LastBearer == "synthetic-rotated-profile-token", "앱이 갱신한 새 토큰을 다음 조회에서 자동으로 다시 읽음");
        handler.CodeForbidden = true;
        source.TickAsync(true).GetAwaiter().GetResult();
        Check(!source.Failed && source.Data is not null, "CLI 사용량 접근 제한 시 유효한 앱 인증으로 연결");
        WriteProfile(profile, expiry: DateTimeOffset.UtcNow.AddHours(-1));
        handler.CodeForbidden = false;
        source.TickAsync(true).GetAwaiter().GetResult();
        Check(source.Failed && source.Status.Contains("갱신 대기"), "만료 앱 OAuth와 쿠키 장애가 겹쳐도 갱신 대기를 유지");
        File.Delete(Path.Combine(profile, "Cookies"));
        source.TickAsync(true).GetAwaiter().GetResult();
        Check(source.Failed && source.Data is null && source.Status.Contains("갱신 대기") && !source.Status.Contains("로그인 필요"), "앱 토큰 만료를 로그아웃으로 단정하지 않고 자동 갱신 대기");
        File.WriteAllText(Path.Combine(profile, "config.json"), "{\"lastKnownAccountUuid\":\"" + Account + "\",\"oauth:tokenCacheV2\":null}");
        source.TickAsync(true).GetAwaiter().GetResult();
        Check(source.Failed && source.Data is null && source.Status.Contains("브라우저 로그인"), "CLI 인증 거부와 앱 캐시 제거가 함께 확인되면 로그인 안내");
        WriteProfile(profile);
        source.TickAsync(true).GetAwaiter().GetResult();
        Check(!source.Failed && source.Data is not null, "앱 로그인 복구 후 모니터 재시작이나 브라우저 버튼 없이 자동 복구");
    }

    private static ClaudeDesktopCredential? ReadProfile(string path)
    {
        try
        {
            var method = typeof(ClaudeDesktopAuth).GetMethod("ReadProfile", BindingFlags.NonPublic | BindingFlags.Static)!;
            var result = method.Invoke(null, new object[] { path });
            return result is ValueTuple<ClaudeDesktopCredential, string?> pair ? pair.Item1 : null;
        }
        catch (TargetInvocationException error) when (error.InnerException is InvalidOperationException)
        { throw (InvalidOperationException)error.InnerException; }
        catch (TargetInvocationException error) when (error.InnerException is IOException) { return null; }
    }

    private static void WriteProfile(string directory, string token = "synthetic-profile-token", DateTimeOffset? expiry = null)
    {
        Directory.CreateDirectory(directory);
        byte[] key = RandomNumberGenerator.GetBytes(32), nonce = RandomNumberGenerator.GetBytes(12);
        byte[] plain = JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object>
        {
            ["acct:" + Account + "|9d1c250a-e61b-44d9-88ed-5944d1962f5e:" + Organization + ":https://api.anthropic.com:user:inference user:file_upload user:profile"] =
                new { token, expiresAt = (expiry ?? DateTimeOffset.UtcNow.AddHours(1)).ToUnixTimeMilliseconds() }
        });
        byte[] cipher = new byte[plain.Length], tag = new byte[16];
        using (var aes = new AesGcm(key, 16)) aes.Encrypt(nonce, plain, cipher, tag);
        byte[] blob = "v10"u8.ToArray().Concat(nonce).Concat(cipher).Concat(tag).ToArray();
        File.WriteAllText(Path.Combine(directory, "config.json"), JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["lastKnownAccountUuid"] = Account, ["oauth:tokenCacheV2"] = Convert.ToBase64String(blob)
        }));
        File.WriteAllText(Path.Combine(directory, "Local State"), JsonSerializer.Serialize(new
        {
            os_crypt = new { encrypted_key = Convert.ToBase64String("DPAPI"u8.ToArray().Concat(Protect(key)).ToArray()) }
        }));
        CryptographicOperations.ZeroMemory(key);
        CryptographicOperations.ZeroMemory(plain);
    }

    [StructLayout(LayoutKind.Sequential)] private struct Blob { public int Length; public IntPtr Data; }
    [DllImport("crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CryptProtectData(ref Blob input, string? description, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, out Blob output);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr pointer);

    private static byte[] Protect(byte[] value)
    {
        var input = new Blob { Length = value.Length, Data = Marshal.AllocHGlobal(value.Length) };
        try
        {
            Marshal.Copy(value, 0, input.Data, value.Length);
            if (!CryptProtectData(ref input, null, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out var output))
                throw new InvalidOperationException("합성 테스트 키 암호화 실패");
            try
            {
                byte[] result = new byte[output.Length];
                Marshal.Copy(output.Data, result, 0, result.Length);
                return result;
            }
            finally { LocalFree(output.Data); }
        }
        finally
        {
            for (int i = 0; i < value.Length; i++) Marshal.WriteByte(input.Data, i, 0);
            Marshal.FreeHGlobal(input.Data);
        }
    }
}
