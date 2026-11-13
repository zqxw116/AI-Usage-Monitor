using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using UsageMonitor;

if (args.Length == 2 && args[0] == "--write-installer")
{
    File.WriteAllText(args[1], AppUpdater.InstallerScript, new System.Text.UTF8Encoding(true));
    return;
}

int passed = 0;
void Check(bool condition, string name)
{
    if (!condition) throw new InvalidOperationException(name);
    Console.WriteLine("PASS " + name);
    passed++;
}
string Release(string tag = "v1.2.0", bool draft = false, bool prerelease = false,
    string? url = null, string? digest = null, long size = 3) => JsonSerializer.Serialize(new
    {
        tag_name = tag, draft, prerelease,
        assets = new[] { new { name = "AI-Usage-Monitor-win-x64.exe", size, state = "uploaded",
            browser_download_url = url ?? "https://github.com/zqxw116/AI-Usage-Monitor/releases/download/" + tag + "/AI-Usage-Monitor-win-x64.exe",
            digest = digest ?? "sha256:" + Convert.ToHexString(SHA256.HashData(new byte[] { 1, 2, 3 })) } }
    });
var current = new Version(1, 1, 0);
bool Rejects(string json)
{
    try { AppUpdater.ParseRelease(json, current); return false; }
    catch (InvalidDataException) { return true; }
}
var release = AppUpdater.ParseRelease(Release(), current);
Check(release?.Version == new Version(1, 2, 0), "새 정식 버전 발견");
Check(AppUpdater.ParseRelease(Release("v1.1.0"), current) is null, "동일 버전 제외");
Check(AppUpdater.ParseRelease(Release("v1.0.0"), current) is null, "이전 버전 제외");
Check(AppUpdater.ParseRelease(Release(draft: true), current) is null, "초안 제외");
Check(AppUpdater.ParseRelease(Release(prerelease: true), current) is null, "프리릴리스 제외");
foreach (var tag in new[] { "v1.2.0-beta", "v1.2.0/evil", "1.2.0", "v1.2", "v1.2.0.4" })
    Check(AppUpdater.ParseRelease(Release(tag), current) is null, "비정식 태그 거부: " + tag);
foreach (var url in new[] { "http://github.com/zqxw116/AI-Usage-Monitor/releases/download/v1.2.0/AI-Usage-Monitor-win-x64.exe",
    "https://evil.example/update.exe", "https://github.com/attacker/repo/releases/download/v1.2.0/AI-Usage-Monitor-win-x64.exe",
    "https://github.com/zqxw116/AI-Usage-Monitor/releases/download/v1.2.0/AI-Usage-Monitor-win-x64.exe?redirect=1" })
    Check(Rejects(Release(url: url)), "다른 다운로드 경로 거부");
foreach (var digest in new[] { "", "sha256:bad", "sha512:" + new string('a', 64) })
    Check(Rejects(Release(digest: digest)), "해시 없는/잘못된 자산 거부");
Check(Rejects(Release(size: 0)), "빈 자산 거부");
Check(Rejects(Release(size: 600_000_000)), "과대 자산 거부");
var folder = Path.Combine(Path.GetTempPath(), "AIUsageMonitor-update-tests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(folder);
try
{
    var destination = Path.Combine(folder, "download.exe");
    using var good = new HttpClient(new Handler(new byte[] { 1, 2, 3 }));
    await AppUpdater.DownloadAsync(good, release!, destination, CancellationToken.None);
    Check(File.ReadAllBytes(destination).SequenceEqual(new byte[] { 1, 2, 3 }), "해시 일치 파일 다운로드");
    using var bad = new HttpClient(new Handler(new byte[] { 3, 2, 1 }));
    bool rejected = false;
    try { await AppUpdater.DownloadAsync(bad, release!, destination, CancellationToken.None); }
    catch (InvalidDataException) { rejected = true; }
    Check(rejected && !File.Exists(destination), "변조된 파일 거부 및 삭제");
    using var truncated = new HttpClient(new Handler(new byte[] { 1, 2 }));
    rejected = false;
    try { await AppUpdater.DownloadAsync(truncated, release!, destination, CancellationToken.None); }
    catch (InvalidDataException) { rejected = true; }
    Check(rejected && !File.Exists(destination), "크기가 다른 파일 거부 및 삭제");
}
finally { Directory.Delete(folder, true); }
Console.WriteLine($"업데이트 합성 검사 {passed}개 통과");

sealed class Handler(byte[] bytes) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) });
}
