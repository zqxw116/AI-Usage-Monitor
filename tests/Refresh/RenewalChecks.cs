using System.Diagnostics;
using UsageMonitor;

internal static class RenewalChecks
{
    internal static async Task RunAsync(Action<bool, string> check)
    {
        string root = Path.Combine(Path.GetTempPath(), "usage-renewal-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string shell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");
            async Task<(string? Error, string[] Calls)> Run(string version, string status, CancellationToken cancellation, TimeSpan? timeout = null)
            {
                string fixture = Path.Combine(root, Guid.NewGuid().ToString("N") + ".ps1");
                string log = fixture + ".calls";
                File.WriteAllText(fixture, "$first = $args[0]; $second = $args[1]\n" +
                    "$ErrorActionPreference = 'Stop'\n" +
                    "Add-Content -LiteralPath ($PSCommandPath + '.calls') -Value ($first + ' ' + $second)\n" +
                    "if ($first -eq '--version') { Write-Output '" + version + "'; exit 0 }\n" +
                    "if ($first -ne 'auth' -or $second -ne 'status') { exit 9 }\n" +
                    "if ($env:CLAUDE_CONFIG_DIR -ne $PSScriptRoot) { exit 8 }\n" + status);
                var command = new OfficialLogin.LoginCommand(shell, new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", fixture, "auth", "login" });
                string? error = null;
                try { await ClaudeAuthRenewal.RunAsync(command, root, cancellation, timeout); }
                catch (InvalidOperationException exception) { error = exception.Message; }
                return (error, File.Exists(log) ? File.ReadAllLines(log) : Array.Empty<string>());
            }

            var old = await Run("2.1.314 (Claude Code)", "exit 0", CancellationToken.None);
            check(old.Error?.Contains("2.1.317") == true && old.Calls.SequenceEqual(new[] { "--version " }), "구버전에서 인증 상태 명령 없이 업데이트 안내");
            var preview = await Run("2.1.317-beta.1 (Claude Code)", "exit 0", CancellationToken.None);
            check(preview.Error is not null && preview.Calls.Length == 1, "시험판 버전을 안전한 안정 버전으로 인정하지 않음");
            var good = await Run("2.1.317 (Claude Code)", "[Console]::Out.Write(('x' * 100000)); [Console]::Error.Write(('y' * 100000)); exit 0", CancellationToken.None);
            check(good.Error is null && good.Calls.SequenceEqual(new[] { "--version ", "auth status" }), "지원 버전에서 추론 없이 인증 상태 명령만 실행하고 양쪽 출력 배출");
            var failed = await Run("2.1.318 (Claude Code)", "Write-Output 'synthetic-secret'; exit 1", CancellationToken.None);
            check(failed.Error is not null && !failed.Error.Contains("synthetic-secret"), "CLI 실패 출력의 비밀값을 오류에 포함하지 않음");
            using var cancellation = new CancellationTokenSource();
            cancellation.CancelAfter(TimeSpan.FromMilliseconds(1200));
            bool cancelled = false;
            var watch = Stopwatch.StartNew();
            try { await Run("2.1.317 (Claude Code)", "Start-Sleep -Seconds 30", cancellation.Token); }
            catch (OperationCanceledException) { cancelled = true; }
            check(cancelled && watch.Elapsed < TimeSpan.FromSeconds(10), "취소 시 소유한 합성 CLI 작업을 정리하고 취소 전달");
            var timedOut = await Run("2.1.317 (Claude Code)", "Start-Sleep -Seconds 30", CancellationToken.None, TimeSpan.FromMilliseconds(1200));
            check(timedOut.Error?.Contains("시간") == true, "인증 갱신 제한시간을 한국어 오류로 보고");
        }
        finally { Directory.Delete(root, true); }
    }
}
