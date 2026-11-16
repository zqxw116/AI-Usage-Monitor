using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace UsageMonitor;

// Do not make this a record: its generated ToString would include credentials.
internal sealed class ClaudeDesktopCredential
{
    public string? Bearer { get; init; }
    public string? SessionKey { get; init; }
    public string? Organization { get; init; }
}

internal static class ClaudeDesktopAuth
{
    private const string ClientId = "9d1c250a-e61b-44d9-88ed-5944d1962f5e";
    private const string ApiHost = "https://api.anthropic.com";
    private const string BaseScope = "user:file_upload user:inference user:profile";
    internal sealed class RenewalPendingException() : InvalidOperationException("Claude 앱 인증 갱신 대기 · 자동 재시도") { }

    public static IEnumerable<string> GetProfileDirectories()
    {
        var paths = new List<string>();
        string roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        paths.Add(Path.Combine(roaming, "Claude"));
        string moved = Path.Combine(local, "Claude-Data");
        paths.Add(moved);
        if (Directory.Exists(moved))
            paths.AddRange(Directory.EnumerateDirectories(moved, "claude_*"));
        string packages = Path.Combine(local, "Packages");
        if (Directory.Exists(packages))
            paths.AddRange(Directory.EnumerateDirectories(packages, "Claude_*")
                .Select(path => Path.Combine(path, "LocalCache", "Roaming", "Claude")));
        return paths.Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public static ClaudeDesktopCredential? Read()
    {
        try
        {
            var found = new List<(ClaudeDesktopCredential Credential, string? Account)>();
            InvalidOperationException? failure = null;
            foreach (string directory in GetProfileDirectories().OrderByDescending(path =>
                File.GetLastWriteTimeUtc(Path.Combine(path, "config.json"))))
            {
                try
                {
                    var result = ReadProfile(directory);
                    if (result.HasValue) found.Add(result.Value);
                }
                catch (Exception error) when (error is DesktopAuthException or RenewalPendingException)
                { failure ??= (InvalidOperationException)error; }
                catch (Exception error) when (IsReadFailure(error))
                { failure ??= new InvalidOperationException("Claude Desktop 로그인 정보를 읽을 수 없습니다."); }
            }
            if (found.Count == 0)
            {
                if (failure is not null) throw failure;
                return null;
            }
            if (found.Select(item => (item.Account, item.Credential.Organization)).Distinct().Count() > 1)
                throw new InvalidOperationException("Claude Desktop 프로필이 여러 계정 또는 조직을 가리킵니다.");
            if (found.Any(item => item.Account is null) && found.Select(item => item.Credential.SessionKey).Distinct().Count() > 1)
                throw new InvalidOperationException("Claude Desktop 활성 로그인 프로필을 하나로 확인할 수 없습니다.");
            return found[0].Credential;
        }
        catch (InvalidOperationException) { throw; }
        catch (Exception error) when (IsReadFailure(error))
        { throw new InvalidOperationException("Claude Desktop 로그인 정보를 읽을 수 없습니다."); }
    }

    private static (ClaudeDesktopCredential Credential, string? Account)? ReadProfile(string directory)
    {
        string cookies = Path.Combine(directory, "Network", "Cookies");
        if (!File.Exists(cookies)) cookies = Path.Combine(directory, "Cookies");
        string configPath = Path.Combine(directory, "config.json");
        using var config = File.Exists(configPath) ? UsageSource.ReadJson(configPath) : null;
        if (config is not null && config.RootElement.ValueKind != JsonValueKind.Object)
            throw Failure("Claude Desktop 설정 형식을 확인할 수 없습니다.");
        string? activeAccount = config is null ? null : StringProperty(config.RootElement, "lastKnownAccountUuid");
        if (activeAccount is not null && !Guid.TryParse(activeAccount, out _))
            throw Failure("Claude Desktop 활성 계정 형식을 확인할 수 없습니다.");
        activeAccount = activeAccount?.ToLowerInvariant();
        byte[]? key = null;
        try
        {
            byte[] GetKey() => key ??= ReadEncryptionKey(directory);
            CachedToken? cached = null;
            InvalidOperationException? cacheFailure = null;
            try { cached = config is null ? null : ReadCachedToken(config.RootElement, activeAccount, null, GetKey); }
            catch (Exception error) when (error is DesktopAuthException or RenewalPendingException)
            { cacheFailure = (InvalidOperationException)error; }
            ClaudeDesktopCredential FromCache() => new() { Bearer = cached!.Token, Organization = cached.Organization };
            if (!File.Exists(cookies))
            {
                if (cached is not null) return (FromCache(), activeAccount);
                if (cacheFailure is not null) throw cacheFailure;
                return null;
            }
            (string? SessionKey, string? Organization, string? Account) session;
            try { session = ReadCookies(cookies, activeAccount, GetKey); }
            catch (Exception error) when (error is DesktopAuthException || IsReadFailure(error))
            {
                // OAuth is a separate grant; cookie absence or read failure does not revoke it.
                if (cached is null)
                {
                    if (cacheFailure is RenewalPendingException) throw cacheFailure;
                    throw;
                }
                return (FromCache(), activeAccount);
            }
            if (session.SessionKey is null)
            {
                if (cached is not null) return (FromCache(), activeAccount);
                if (cacheFailure is not null) throw cacheFailure;
                return null;
            }
            string? bearer = session.Organization is null ? cached?.Token : config is null ? null :
                ReadCachedToken(config.RootElement, session.Account, session.Organization, GetKey)?.Token;
            return (new ClaudeDesktopCredential { Bearer = bearer, SessionKey = session.SessionKey, Organization = session.Organization }, session.Account);
        }
        finally { if (key is not null) CryptographicOperations.ZeroMemory(key); }
    }

    private sealed class CachedToken(string token, string organization)
    {
        public string Token { get; } = token;
        public string Organization { get; } = organization;
    }

    private static CachedToken? ReadCachedToken(JsonElement root, string? account, string? organization, Func<byte[]> getKey)
    {
        if (account is null) return null;
        bool hasV2 = root.TryGetProperty("oauth:tokenCacheV2", out var encrypted);
        if (!hasV2) root.TryGetProperty("oauth:tokenCache", out encrypted);
        if (encrypted.ValueKind != JsonValueKind.String) return null;
        byte[]? blob = null, plaintext = null;
        try
        {
            blob = Convert.FromBase64String(encrypted.GetString()!);
            plaintext = DecryptV10(blob, getKey());
            using var cache = JsonDocument.Parse(plaintext);
            var selected = SelectTokenCache(cache.RootElement, account, organization, DateTimeOffset.UtcNow);
            if (selected is null && SelectTokenCache(cache.RootElement, account, organization, DateTimeOffset.MinValue) is not null)
                throw new RenewalPendingException();
            return selected;
        }
        // If cookies worked, an unavailable cache must not prevent use of that web session.
        catch (Exception error) when (organization is not null && (error is DesktopAuthException or RenewalPendingException)) { return null; }
        catch (Exception error) when (IsReadFailure(error)) { return null; }
        finally
        {
            if (blob is not null) CryptographicOperations.ZeroMemory(blob);
            if (plaintext is not null) CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    // Pure parser for synthetic fixtures. The key carries the scope, not a field in the value.
    internal static string? ParseTokenCache(JsonElement cache, string? account, string? organization, DateTimeOffset now)
        => SelectTokenCache(cache, account, organization, now)?.Token;

    private static CachedToken? SelectTokenCache(JsonElement cache, string? account, string? organization, DateTimeOffset now)
    {
        if (cache.ValueKind != JsonValueKind.Object) throw Failure("Claude Desktop OAuth 캐시 형식이 지원되지 않습니다.");
        if (account is null) return null;
        string prefix = "acct:" + account.ToLowerInvariant() + "|";
        var candidates = new List<(string Token, string Organization, int Priority)>();
        foreach (var item in cache.EnumerateObject())
        {
            string name = item.Name;
            if (!name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
            name = name[prefix.Length..];
            if (!name.StartsWith(ClientId + ":", StringComparison.Ordinal)) continue;
            string remainder = name[(ClientId.Length + 1)..];
            int separator = remainder.IndexOf(":" + ApiHost + ":", StringComparison.Ordinal);
            if (separator < 0) continue;
            string org = remainder[..separator];
            if (!Guid.TryParse(org, out _)) continue;
            if (organization is not null && !org.Equals(organization, StringComparison.OrdinalIgnoreCase)) continue;
            string scope = remainder[(separator + ApiHost.Length + 2)..];
            // OAuth scopes are a set. Desktop sign-in and Code caches use different orderings.
            scope = string.Join(" ", scope.Split(' ').Order(StringComparer.Ordinal));
            int priority = scope == BaseScope ? 0 : scope == BaseScope + " user:sessions:claude_code" ? 1 : -1;
            if (priority < 0 || item.Value.ValueKind != JsonValueKind.Object) continue;
            string? token = StringProperty(item.Value, "token");
            if (string.IsNullOrWhiteSpace(token) || token.Any(char.IsWhiteSpace) || !item.Value.TryGetProperty("expiresAt", out var expires) ||
                expires.ValueKind != JsonValueKind.Number || !expires.TryGetInt64(out long expiry) || expiry <= 0 || expiry <= now.ToUnixTimeMilliseconds()) continue;
            candidates.Add((token, org, priority));
        }
        if (organization is null && candidates.Select(item => item.Organization).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1)
            throw Failure("Claude 앱의 활성 조직을 확인할 수 없습니다. 여러 조직의 인증 캐시가 있습니다.");
        var selected = candidates.OrderBy(item => item.Priority).FirstOrDefault();
        return selected.Token is null ? null : new CachedToken(selected.Token, Guid.Parse(selected.Organization).ToString());
    }

    internal static byte[] DecryptV10(byte[] encrypted, byte[] key, string? cookieHost = null, int cookieVersion = 0)
    {
        if (encrypted.Length >= 3 && encrypted.AsSpan(0, 3).SequenceEqual("v20"u8))
            throw Failure("Claude Desktop v20 암호화 형식은 지원되지 않습니다.");
        if (encrypted.Length < 31 || !encrypted.AsSpan(0, 3).SequenceEqual("v10"u8) || key.Length != 32)
            throw Failure("Claude Desktop 암호화 형식은 지원되지 않습니다.");
        byte[] plaintext = new byte[encrypted.Length - 31];
        try
        {
            using var aes = new AesGcm(key, 16);
            aes.Decrypt(encrypted.AsSpan(3, 12), encrypted.AsSpan(15, plaintext.Length), encrypted.AsSpan(encrypted.Length - 16), plaintext);
            if (cookieHost is null || cookieVersion < 24) return plaintext;
            byte[] host = Encoding.UTF8.GetBytes(cookieHost);
            byte[] digest = SHA256.HashData(host);
            try
            {
                if (plaintext.Length < digest.Length || !CryptographicOperations.FixedTimeEquals(plaintext.AsSpan(0, digest.Length), digest))
                    throw Failure("Claude Desktop 쿠키 호스트 검증에 실패했습니다.");
                byte[] value = plaintext[digest.Length..];
                CryptographicOperations.ZeroMemory(plaintext);
                return value;
            }
            finally { CryptographicOperations.ZeroMemory(host); CryptographicOperations.ZeroMemory(digest); }
        }
        catch (DesktopAuthException) { CryptographicOperations.ZeroMemory(plaintext); throw; }
        catch (CryptographicException)
        {
            CryptographicOperations.ZeroMemory(plaintext);
            throw Failure("Claude Desktop 로그인 정보 복호화에 실패했습니다.");
        }
    }

    private static byte[] ReadEncryptionKey(string directory)
    {
        using var state = UsageSource.ReadJson(Path.Combine(directory, "Local State"));
        if (state.RootElement.ValueKind != JsonValueKind.Object || !state.RootElement.TryGetProperty("os_crypt", out var crypt) || StringProperty(crypt, "encrypted_key") is not string encoded)
            throw Failure("Claude Desktop 암호화 키 형식은 지원되지 않습니다.");
        byte[] wrapped = Convert.FromBase64String(encoded);
        try
        {
            if (wrapped.Length <= 5 || !wrapped.AsSpan(0, 5).SequenceEqual("DPAPI"u8))
                throw Failure("Claude Desktop 암호화 키 형식은 지원되지 않습니다.");
            byte[] key = Unprotect(wrapped.AsSpan(5));
            if (key.Length == 32) return key;
            CryptographicOperations.ZeroMemory(key);
            throw Failure("Claude Desktop 암호화 키 형식은 지원되지 않습니다.");
        }
        finally { CryptographicOperations.ZeroMemory(wrapped); }
    }

    private static byte[] Unprotect(ReadOnlySpan<byte> encrypted)
    {
        IntPtr input = Marshal.AllocHGlobal(encrypted.Length);
        var output = new DataBlob();
        byte[] copy = encrypted.ToArray();
        try
        {
            Marshal.Copy(copy, 0, input, copy.Length);
            var blob = new DataBlob { Length = copy.Length, Data = input };
            // No machine scope or optional entropy: Chromium uses the current Windows user.
            if (!CryptUnprotectData(ref blob, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out output))
                throw Failure("Claude Desktop 암호화 키를 현재 Windows 계정으로 열 수 없습니다.");
            byte[] result = new byte[output.Length];
            Marshal.Copy(output.Data, result, 0, result.Length);
            return result;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(copy);
            for (int i = 0; i < encrypted.Length; i++) Marshal.WriteByte(input, i, 0);
            Marshal.FreeHGlobal(input);
            if (output.Data != IntPtr.Zero)
            {
                for (int i = 0; i < output.Length; i++) Marshal.WriteByte(output.Data, i, 0);
                LocalFree(output.Data);
            }
        }
    }

    private static (string? SessionKey, string? Organization, string? Account) ReadCookies(string path, string? activeAccount, Func<byte[]> getKey)
    {
        IntPtr database = IntPtr.Zero;
        try
        {
            int result = sqlite3_open_v2(path, out database, 1, IntPtr.Zero);
            if (result != 0)
            {
                int extended = database == IntPtr.Zero ? result : sqlite3_extended_errcode(database);
                int system = 0;
                if (database != IntPtr.Zero)
                {
                    try { system = sqlite3_system_errno(database); }
                    catch (EntryPointNotFoundException) { }
                }
                throw new CookieOpenException($"Claude 쿠키 접근 불가 (SQLite {extended}, OS {system})");
            }
            sqlite3_busy_timeout(database, 250);
            int version = 0;
            Query(database, "SELECT value FROM meta WHERE key='version'", row =>
            {
                if (!int.TryParse(ColumnText(row, 0), out version))
                    throw Failure("Claude Desktop 쿠키 버전을 확인할 수 없습니다.");
            });
            if (version <= 0) throw Failure("Claude Desktop 쿠키 버전을 확인할 수 없습니다.");
            var sessions = new List<(string Value, string? Account)>();
            var organizations = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            long now = (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + 11644473600000L) * 1000;
            Query(database, "SELECT host_key,name,value,encrypted_value,is_httponly,is_secure,expires_utc FROM cookies WHERE name IN ('sessionKey','lastActiveOrg') AND host_key IN ('claude.ai','.claude.ai')", row =>
            {
                long expiry = sqlite3_column_int64(row, 6);
                if (expiry > 0 && expiry <= now) return;
                string host = ColumnText(row, 0), name = ColumnText(row, 1);
                if (name == "sessionKey" && (sqlite3_column_int64(row, 4) == 0 || sqlite3_column_int64(row, 5) == 0)) return;
                byte[] encrypted = ColumnBytes(row, 3);
                byte[]? plaintext = null;
                string value;
                try
                {
                    if (encrypted.Length > 0)
                    {
                        plaintext = DecryptV10(encrypted, getKey(), host, version);
                        value = Encoding.UTF8.GetString(plaintext);
                    }
                    else value = ColumnText(row, 2);
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(encrypted);
                    if (plaintext is not null) CryptographicOperations.ZeroMemory(plaintext);
                }
                if (name == "lastActiveOrg")
                {
                    if (Guid.TryParse(value, out var org)) organizations.Add(org.ToString());
                    return;
                }
                if (string.IsNullOrWhiteSpace(value)) return;
                string? account = SessionAccount(value);
                if (activeAccount is not null && account is not null && account != activeAccount) return;
                sessions.Add((value, account ?? activeAccount));
            });
            var distinct = sessions.Distinct().ToArray();
            if (distinct.Length == 0) return (null, null, null);
            if (distinct.Length > 1) throw Failure("Claude Desktop 활성 로그인 세션을 하나로 확인할 수 없습니다.");
            if (organizations.Count > 1) throw Failure("Claude Desktop 활성 조직을 하나로 확인할 수 없습니다.");
            return (distinct[0].Value, organizations.SingleOrDefault(), distinct[0].Account);
        }
        finally { if (database != IntPtr.Zero) sqlite3_close(database); }
    }

    private static string? SessionAccount(string session)
    {
        if (!session.StartsWith("sk-ant-sid02-", StringComparison.Ordinal) || session.Length < 35) return null;
        byte[] bytes;
        try { bytes = Convert.FromBase64String(session.Substring(13, 22).Replace('-', '+').Replace('_', '/') + "=="); }
        catch (FormatException) { return null; }
        try
        {
            if (bytes.Length != 16) return null;
            string hex = Convert.ToHexString(bytes).ToLowerInvariant();
            return $"{hex[..8]}-{hex[8..12]}-{hex[12..16]}-{hex[16..20]}-{hex[20..]}";
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    private static void Query(IntPtr database, string sql, Action<IntPtr> read)
    {
        IntPtr statement = IntPtr.Zero;
        try
        {
            if (sqlite3_prepare_v2(database, sql, -1, out statement, IntPtr.Zero) != 0 || sqlite3_stmt_readonly(statement) == 0)
                throw Failure("Claude Desktop 쿠키 형식을 읽을 수 없습니다.");
            int status;
            while ((status = sqlite3_step(statement)) == 100) read(statement);
            if (status != 101) throw Failure("Claude Desktop 쿠키를 읽을 수 없습니다. 앱 상태가 안정된 후 다시 시도하세요.");
        }
        finally { if (statement != IntPtr.Zero) sqlite3_finalize(statement); }
    }

    private static string ColumnText(IntPtr statement, int column) => Marshal.PtrToStringUTF8(sqlite3_column_text(statement, column)) ?? "";
    private static byte[] ColumnBytes(IntPtr statement, int column)
    {
        int length = sqlite3_column_bytes(statement, column);
        byte[] result = new byte[length];
        if (length > 0) Marshal.Copy(sqlite3_column_blob(statement, column), result, 0, length);
        return result;
    }

    private static string? StringProperty(JsonElement value, string name) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String ? property.GetString() : null;
    private static bool IsReadFailure(Exception error) => error is IOException or UnauthorizedAccessException or JsonException or FormatException or CryptographicException or DllNotFoundException or EntryPointNotFoundException or ArgumentException;
    private static DesktopAuthException Failure(string message) => new(message);
    private class DesktopAuthException(string message) : InvalidOperationException(message) { }
    private sealed class CookieOpenException(string message) : DesktopAuthException(message) { }

    [StructLayout(LayoutKind.Sequential)] private struct DataBlob { public int Length; public IntPtr Data; }
    [DllImport("crypt32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(ref DataBlob input, IntPtr description, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, out DataBlob output);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr memory);
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_open_v2([MarshalAs(UnmanagedType.LPUTF8Str)] string path, out IntPtr database, int flags, IntPtr vfs);
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_close(IntPtr database);
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_extended_errcode(IntPtr database);
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_system_errno(IntPtr database);
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_busy_timeout(IntPtr database, int milliseconds);
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_prepare_v2(IntPtr database, [MarshalAs(UnmanagedType.LPUTF8Str)] string sql, int bytes, out IntPtr statement, IntPtr tail);
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_stmt_readonly(IntPtr statement);
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_step(IntPtr statement);
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_finalize(IntPtr statement);
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)] private static extern IntPtr sqlite3_column_text(IntPtr statement, int column);
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)] private static extern IntPtr sqlite3_column_blob(IntPtr statement, int column);
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_column_bytes(IntPtr statement, int column);
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)] private static extern long sqlite3_column_int64(IntPtr statement, int column);
}
