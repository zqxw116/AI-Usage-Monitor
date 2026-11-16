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
    string? url = null, string? digest = null, long size = 3, string? notes = null) => JsonSerializer.Serialize(new
    {
        tag_name = tag, draft, prerelease, body = notes,
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
var withNotes = AppUpdater.ParseRelease(Release(notes: "## 변경 내용\n- 새로고침 개선"), current)!;
Check(withNotes.ReleaseNotes == "## 변경 내용\n- 새로고침 개선" && withNotes.VersionText == "1.2.0", "릴리스 본문과 표시 버전 읽기");
Check(release!.ReleaseNotes == "", "릴리스 본문이 없으면 빈 변경 내용으로 처리");
var largeNotes = AppUpdater.ParseRelease(Release(notes: new string('가', 20000)), current)!;
Check(System.Text.Encoding.UTF8.GetByteCount(largeNotes.ReleaseNotes) <= 16 * 1024 && !largeNotes.ReleaseNotes.Contains('\uFFFD'), "긴 한글 본문을 UTF8 16KiB 이내에서 문자 손상 없이 제한");
using (var metadata = new HttpClient(new MetadataHandler(Release(notes: "새 버전 변경 내용"))))
{
    var installed = await AppUpdater.GetCurrentReleaseAsync(metadata, new Version(1, 2, 0), CancellationToken.None);
    Check(installed?.ReleaseNotes == "새 버전 변경 내용", "기존 업데이터에서 설치한 현재 버전도 공개 릴리스 본문 조회");
    Check(await AppUpdater.GetCurrentReleaseAsync(metadata, new Version(1, 2, 1), CancellationToken.None) is null, "현재 버전과 다른 릴리스 응답을 공지로 사용하지 않음");
}
using (var missing = new HttpClient(new MetadataHandler("", HttpStatusCode.NotFound)))
    Check(await AppUpdater.GetCurrentReleaseAsync(missing, new Version(1, 2, 0), CancellationToken.None) is null, "아직 배포되지 않은 현재 버전은 공지 없이 처리");
Check(AppUpdater.SamePayload(withNotes, AppUpdater.ParseRelease(Release(notes: "수정된 변경 내용"), current)!), "본문만 수정된 릴리스는 동일한 설치 파일로 인정");
Check(!AppUpdater.SamePayload(withNotes, withNotes with { Size = 4 }), "설치 파일 정보 변경은 계속 거부");
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
    string notices = Path.Combine(folder, "notices");
    Check(UpdateNoticeStore.LoadForCurrentVersion(withNotes.Version, notices) is null, "저장된 공지가 없으면 표시하지 않음");
    Check(UpdateNoticeStore.Save(withNotes, notices), "설치 예정 버전 변경 내용을 저장");
    var notice = UpdateNoticeStore.LoadForCurrentVersion(withNotes.Version, notices);
    Check(notice?.Notes == withNotes.ReleaseNotes && notice.ReleasePage == withNotes.ReleasePage && !notice.IsRead, "현재 버전의 변경 내용과 미읽음 상태 복원");
    Check(UpdateNoticeStore.LoadForCurrentVersion(withNotes.Version, notices)?.IsRead == false, "같은 버전 재실행 후에도 미읽음 유지");
    Check(UpdateNoticeStore.LoadForCurrentVersion(current, notices) is null, "설치 실패로 이전 버전이 실행되면 다음 버전 공지 표시 안 함");
    Check(!UpdateNoticeStore.Acknowledge(current, notices), "다른 버전의 공지를 읽음 처리하지 않음");
    Check(UpdateNoticeStore.Acknowledge(withNotes.Version, notices), "사용자가 확인한 현재 버전 공지를 읽음으로 저장");
    Check(UpdateNoticeStore.LoadForCurrentVersion(withNotes.Version, notices)?.IsRead == true, "확인 후 재실행하면 읽음 상태 유지");
    Check(UpdateNoticeStore.Save(withNotes, notices) && UpdateNoticeStore.LoadForCurrentVersion(withNotes.Version, notices)?.IsRead == true, "같은 버전 저장을 반복해도 읽음 상태를 되돌리지 않음");
    string noticePath = Path.Combine(notices, "update-notice.json");
    using (var metadataOnly = JsonDocument.Parse(File.ReadAllText(noticePath)))
        Check(metadataOnly.RootElement.EnumerateObject().Select(item => item.Name).Order().SequenceEqual(new[] { "notes", "read", "url", "version" }), "공지 파일에는 버전·본문·공식 주소·읽음 상태만 저장");
    File.WriteAllText(noticePath, "{broken");
    Check(UpdateNoticeStore.LoadForCurrentVersion(withNotes.Version, notices) is null, "손상된 공지 JSON은 안전하게 무시");
    File.WriteAllText(noticePath, JsonSerializer.Serialize(new { version = "1.2.0", notes = "synthetic", url = "https://evil.example/release", read = false }));
    Check(UpdateNoticeStore.LoadForCurrentVersion(withNotes.Version, notices) is null, "공식 릴리스 페이지가 아닌 저장 주소 거부");
    File.WriteAllText(noticePath, JsonSerializer.Serialize(new { version = "1.2.0", notes = new string('x', 16 * 1024 + 1), url = withNotes.ReleasePage.AbsoluteUri, read = false }));
    Check(UpdateNoticeStore.LoadForCurrentVersion(withNotes.Version, notices) is null, "저장 파일의 본문도 16KiB를 넘으면 표시하지 않음");
    Check(UpdateNoticeStore.Save(withNotes, notices), "손상된 공지 이후 정상 저장으로 복구");
    using (var locked = new FileStream(noticePath, FileMode.Open, FileAccess.Read, FileShare.Read))
        Check(!UpdateNoticeStore.Acknowledge(withNotes.Version, notices), "읽음 상태 저장 실패는 실패로 반환하여 UI에서 재시도 가능");
    Check(UpdateNoticeStore.LoadForCurrentVersion(withNotes.Version, notices)?.IsRead == false && UpdateNoticeStore.Acknowledge(withNotes.Version, notices), "저장 실패 후 미읽음을 유지하고 다시 확인 가능");
    Check(!Directory.EnumerateFiles(notices, "*.tmp").Any(), "원자 저장의 임시 파일을 남기지 않음");
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

sealed class MetadataHandler(string body, HttpStatusCode status = HttpStatusCode.OK) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.RequestUri?.Scheme != "https" || request.RequestUri.Host != "api.github.com" ||
            !request.RequestUri.AbsolutePath.StartsWith("/repos/zqxw116/AI-Usage-Monitor/releases/tags/v", StringComparison.Ordinal) ||
            request.Headers.Authorization is not null)
            throw new InvalidOperationException("현재 버전 공지는 공식 공개 API에서 무인증 조회해야 합니다.");
        return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body) });
    }
}
