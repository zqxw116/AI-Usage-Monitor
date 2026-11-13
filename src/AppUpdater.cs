using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace UsageMonitor;

internal sealed record UpdateRelease(Version Version, Uri DownloadUrl, string Sha256, long Size)
{
    internal Uri ReleasePage => new($"https://github.com/zqxw116/AI-Usage-Monitor/releases/tag/v{Version.ToString(3)}");
}

internal static class AppUpdater
{
    private const string Repository = "https://github.com/zqxw116/AI-Usage-Monitor";
    private const string AssetName = "AI-Usage-Monitor-win-x64.exe";
    private const long MaximumSize = 500_000_000;
    private static readonly HttpClient Http = CreateHttp();
    internal static Version CurrentVersion
    {
        get
        {
            var version = typeof(AppUpdater).Assembly.GetName().Version ?? new Version(1, 0, 0);
            return new Version(version.Major, version.Minor, Math.Max(0, version.Build));
        }
    }

    private static HttpClient CreateHttp()
    {
        var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = Timeout.InfiniteTimeSpan };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("AI-Usage-Monitor/" + CurrentVersion.ToString(3));
        return http;
    }

    internal static async Task<UpdateRelease?> CheckAsync(CancellationToken cancellation = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.github.com/repos/zqxw116/AI-Usage-Monitor/releases/latest");
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
        using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        await response.Content.LoadIntoBufferAsync(1_000_000, timeout.Token);
        return ParseRelease(await response.Content.ReadAsStringAsync(timeout.Token), CurrentVersion);
    }

    internal static UpdateRelease? ParseRelease(string json, Version current)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (!root.TryGetProperty("draft", out var draft) || draft.ValueKind != JsonValueKind.False ||
            !root.TryGetProperty("prerelease", out var prerelease) || prerelease.ValueKind != JsonValueKind.False ||
            !root.TryGetProperty("tag_name", out var tagValue) || tagValue.ValueKind != JsonValueKind.String) return null;
        string tag = tagValue.GetString()!;
        if (!Regex.IsMatch(tag, @"^v(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)$") ||
            !Version.TryParse(tag[1..], out var version) || version <= current) return null;
        if (!root.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("새 릴리스에 검증 가능한 Windows 실행 파일이 없습니다.");
        string expectedUrl = Repository + "/releases/download/" + tag + "/" + AssetName;
        foreach (var asset in assets.EnumerateArray())
        {
            if (!asset.TryGetProperty("name", out var name) || name.GetString() != AssetName ||
                !asset.TryGetProperty("state", out var state) || state.GetString() != "uploaded" ||
                !asset.TryGetProperty("size", out var sizeValue) || !sizeValue.TryGetInt64(out long size) || size <= 0 || size > MaximumSize ||
                !asset.TryGetProperty("browser_download_url", out var urlValue) || urlValue.GetString() != expectedUrl ||
                !asset.TryGetProperty("digest", out var digestValue) || digestValue.ValueKind != JsonValueKind.String) continue;
            string digest = digestValue.GetString()!;
            if (Regex.IsMatch(digest, "^sha256:[0-9a-fA-F]{64}$"))
                return new UpdateRelease(version, new Uri(expectedUrl), digest[7..], size);
        }
        throw new InvalidDataException("새 릴리스의 Windows 실행 파일, 다운로드 주소 또는 SHA256 정보를 검증할 수 없습니다.");
    }

    // The caller exits only after the verified payload and installer are ready.
    internal static async Task InstallAsync(UpdateRelease release, CancellationToken cancellation = default)
    {
        // Refresh trusted metadata rather than trusting stale UI state or a caller-created record.
        var latest = await CheckAsync(cancellation);
        if (latest != release) throw new InvalidOperationException("릴리스 정보가 변경되었습니다. 업데이트를 다시 확인하세요.");
        string target = Environment.ProcessPath ?? throw new InvalidOperationException("실행 파일 경로를 확인할 수 없습니다.");
        string executableName = Path.GetFileName(target);
        if (!string.Equals(executableName, "AI Usage Monitor.exe", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(executableName, AssetName, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("배포된 AI Usage Monitor.exe 또는 AI-Usage-Monitor-win-x64.exe에서 업데이트를 실행하세요.");
        string stage = Path.Combine(Path.GetDirectoryName(target)!, ".ai-usage-update-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stage); // Fail before download if this installation folder is read-only.
        bool started = false;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            timeout.CancelAfter(TimeSpan.FromMinutes(10));
            string payload = Path.Combine(stage, "update.exe");
            await DownloadAsync(Http, release, payload, timeout.Token);
            string script = Path.Combine(stage, "install.ps1");
            await File.WriteAllTextAsync(script, InstallerScript, new UTF8Encoding(true), timeout.Token);
            timeout.Token.ThrowIfCancellationRequested();
            var start = new ProcessStartInfo
            {
                FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), @"WindowsPowerShell\v1.0\powershell.exe"),
                UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden
            };
            foreach (string argument in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", script,
                "-ParentId", Environment.ProcessId.ToString(), "-Target", target, "-ExpectedHash", release.Sha256 })
                start.ArgumentList.Add(argument);
            using var helper = Process.Start(start) ?? throw new InvalidOperationException("업데이트 도우미를 시작할 수 없습니다.");
            // The helper signals that PowerShell policy and script parsing succeeded before we close the app.
            string ready = Path.Combine(stage, "ready");
            for (int i = 0; i < 100 && !File.Exists(ready); i++)
            {
                if (helper.HasExited) throw new InvalidOperationException("업데이트 도우미가 종료되었습니다. 폴더 권한이나 PowerShell 정책을 확인하세요.");
                await Task.Delay(100, cancellation);
            }
            if (!File.Exists(ready)) throw new InvalidOperationException("업데이트 도우미 응답 시간이 초과되었습니다.");
            started = true;
        }
        finally
        {
            if (!started)
            {
                // A started helper sees this cancellation marker before replacing anything.
                try { File.WriteAllText(Path.Combine(stage, "cancel"), ""); } catch { }
                try { Directory.Delete(stage, true); } catch { }
            }
        }
    }

    internal static async Task DownloadAsync(HttpClient http, UpdateRelease release, string destination, CancellationToken cancellation)
    {
        try
        {
            Uri url = release.DownloadUrl;
            for (int redirects = 0; redirects < 6; redirects++)
            {
                using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellation);
                if ((int)response.StatusCode is 301 or 302 or 303 or 307 or 308)
                {
                    var location = response.Headers.Location ?? throw new InvalidDataException("다운로드 이동 주소가 없습니다.");
                    url = new Uri(url, location);
                    if (url.Scheme != "https" || !url.IsDefaultPort || url.UserInfo.Length != 0 ||
                        (url.Host != "release-assets.githubusercontent.com" && url.Host != "objects.githubusercontent.com"))
                        throw new InvalidDataException("허용되지 않은 업데이트 다운로드 서버입니다.");
                    continue;
                }
                response.EnsureSuccessStatusCode();
                if (response.Content.Headers.ContentLength is long size && size != release.Size)
                    throw new InvalidDataException("업데이트 파일 크기가 일치하지 않습니다.");
                await using var input = await response.Content.ReadAsStreamAsync(cancellation);
                await using (var output = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true))
                {
                    using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                    byte[] buffer = new byte[81920];
                    long total = 0;
                    int count;
                    while ((count = await input.ReadAsync(buffer, cancellation)) > 0)
                    {
                        total += count;
                        if (total > release.Size) throw new InvalidDataException("업데이트 파일이 예상 크기를 초과했습니다.");
                        hash.AppendData(buffer, 0, count);
                        await output.WriteAsync(buffer.AsMemory(0, count), cancellation);
                    }
                    if (total != release.Size || !Convert.ToHexString(hash.GetHashAndReset()).Equals(release.Sha256, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("업데이트 파일의 SHA256 검증에 실패했습니다.");
                }
                return;
            }
            throw new InvalidDataException("업데이트 다운로드 이동 횟수를 초과했습니다.");
        }
        catch
        {
            try { File.Delete(destination); } catch { }
            throw;
        }
    }

    internal const string InstallerScript = """
        param([int]$ParentId, [string]$Target, [string]$ExpectedHash)
        $ErrorActionPreference = 'Stop'
        $stage = [IO.Path]::GetFullPath($PSScriptRoot)
        $Target = [IO.Path]::GetFullPath($Target)
        if ([IO.Path]::GetDirectoryName($stage) -ne [IO.Path]::GetDirectoryName($Target) -or
            [IO.Path]::GetFileName($stage) -notmatch '^\.ai-usage-update-[0-9a-f]{32}$') {
            throw 'Invalid update staging directory'
        }
        $payload = Join-Path $stage 'update.exe'
        $backup = Join-Path $stage 'previous.exe'
        $replaced = $false
        try {
            if ($ExpectedHash -notmatch '^[0-9a-fA-F]{64}$') { throw 'Invalid SHA256' }
            [IO.File]::WriteAllText((Join-Path $stage 'ready'), '')
            $parentProcess = Get-Process -Id $ParentId -ErrorAction SilentlyContinue
            if ($null -ne $parentProcess) {
                if (-not $parentProcess.WaitForExit(120000)) { throw 'The app did not exit within two minutes.' }
            }
            if (Test-Path -LiteralPath (Join-Path $stage 'cancel')) { exit 1 }
            $sha = [Security.Cryptography.SHA256]::Create()
            $stream = [IO.File]::OpenRead($payload)
            try { $actual = [BitConverter]::ToString($sha.ComputeHash($stream)).Replace('-', '') }
            finally { $stream.Dispose(); $sha.Dispose() }
            if ($actual -ne $ExpectedHash) { throw 'SHA256 mismatch' }
            for ($attempt = 0; $attempt -lt 30; $attempt++) {
                try { [IO.File]::Replace($payload, $Target, $backup); $replaced = $true; break }
                catch { if ($attempt -eq 29) { throw }; Start-Sleep -Milliseconds 500 }
            }
            Start-Process -FilePath $Target -WorkingDirectory ([IO.Path]::GetDirectoryName($Target)) -WindowStyle Hidden
            Remove-Item -LiteralPath $stage -Recurse -Force -ErrorAction SilentlyContinue
        }
        catch {
            $details = $_.Exception.Message
            if ($replaced -and (Test-Path -LiteralPath $backup)) {
                try {
                    [IO.File]::Replace($backup, $Target, $null)
                    Start-Process -FilePath $Target -WorkingDirectory ([IO.Path]::GetDirectoryName($Target)) -WindowStyle Hidden
                } catch { $details += "`r`nRecovery: " + $_.Exception.Message }
            }
            [IO.File]::WriteAllText((Join-Path $stage 'error.txt'), $details)
            Add-Type -AssemblyName System.Windows.Forms
            [System.Windows.Forms.MessageBox]::Show("업데이트를 완료하지 못했습니다. 기존 실행 파일과 백업을 확인하세요.`r`n" + $stage + "`r`n" + $details, 'AI Usage Monitor 업데이트') | Out-Null
            exit 1
        }
        """;
}
