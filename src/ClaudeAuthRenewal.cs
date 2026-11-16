using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace UsageMonitor;

internal static class ClaudeAuthRenewal
{
    internal static Task RenewAsync(string configDirectory, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        var command = OfficialLogin.FindCommand(true) ?? throw new InvalidOperationException("Claude Code 설치 확인 필요 · 공식 CLI를 설치한 뒤 다시 시도하세요.");
        return RunAsync(command, configDirectory, cancellation);
    }

    internal static async Task RunAsync(OfficialLogin.LoginCommand command, string configDirectory,
        CancellationToken cancellation, TimeSpan? timeout = null)
    {
        cancellation.ThrowIfCancellationRequested();
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        operation.CancelAfter(timeout ?? TimeSpan.FromSeconds(30));
        if (command.Arguments.Count < 2 || command.Arguments[^2] != "auth" || command.Arguments[^1] != "login")
            throw new InvalidOperationException("Claude Code 공식 로그인 명령 확인 필요");
        var prefix = command.Arguments.Take(command.Arguments.Count - 2).ToArray();
        try
        {
            var versionResult = await ExecuteAsync(command.FileName, prefix.Append("--version"), configDirectory, true, operation.Token);
            var match = Regex.Match(versionResult.Output, @"\A\s*(\d+)\.(\d+)\.(\d+)(?:\s+\(Claude Code\))?\s*\z", RegexOptions.CultureInvariant);
            if (versionResult.ExitCode != 0 || !match.Success || !Version.TryParse(match.Groups[1].Value + "." + match.Groups[2].Value + "." + match.Groups[3].Value, out var version) || version < new Version(2, 1, 317))
                throw new InvalidOperationException("Claude Code 업데이트 필요 · 안정 버전 2.1.317 이상을 설치한 뒤 다시 시도하세요.");

            // v2.1.317 fixes auth status exiting before a token refresh is saved.
            // The caller still re-reads credentials to verify that renewal actually succeeded.
            var status = await ExecuteAsync(command.FileName, prefix.Concat(new[] { "auth", "status" }), configDirectory, false, operation.Token);
            if (status.ExitCode != 0) throw new InvalidOperationException("Claude Code 인증 갱신 확인 필요 · 공식 로그인을 다시 진행하세요.");
        }
        catch (OperationCanceledException) when (operation.IsCancellationRequested)
        {
            if (cancellation.IsCancellationRequested)
                throw new OperationCanceledException("Claude Code 인증 갱신이 취소되었습니다. 다음 시도에도 인증 오류가 나면 공식 로그인을 다시 진행하세요.", cancellation);
            throw new InvalidOperationException("Claude Code 인증 갱신 제한 시간 30초가 지났습니다. 다시 시도하고 인증 오류가 계속되면 공식 로그인을 진행하세요.");
        }
        catch (Exception error) when (error is Win32Exception or IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException("Claude Code 인증 갱신 프로그램 실행 실패 · 설치 상태를 확인하세요.");
        }
    }

    private static async Task<(int ExitCode, string Output)> ExecuteAsync(string fileName, IEnumerable<string> arguments,
        string configDirectory, bool captureVersion, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        var info = new ProcessStartInfo(fileName)
        {
            WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (string argument in arguments) info.ArgumentList.Add(argument);
        info.Environment["CLAUDE_CONFIG_DIR"] = configDirectory;
        using var process = new Process { StartInfo = info };
        if (!process.Start()) throw new InvalidOperationException("Claude Code 인증 갱신 프로그램을 시작할 수 없습니다.");
        process.StandardInput.Close();
        var stdout = DrainAsync(process.StandardOutput, captureVersion, cancellation);
        var stderr = DrainAsync(process.StandardError, false, cancellation);
        try
        {
            await process.WaitForExitAsync(cancellation).ConfigureAwait(false);
            await Task.WhenAll(stdout, stderr).ConfigureAwait(false);
            cancellation.ThrowIfCancellationRequested();
            return (process.ExitCode, await stdout.ConfigureAwait(false));
        }
        catch
        {
            // Only the process started above belongs to this operation.
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (Exception error) when (error is InvalidOperationException or Win32Exception or NotSupportedException) { }
            try { await Task.WhenAll(stdout, stderr).ConfigureAwait(false); }
            catch (Exception error) when (error is OperationCanceledException or IOException or ObjectDisposedException) { }
            throw;
        }
    }

    private static async Task<string> DrainAsync(StreamReader reader, bool capture, CancellationToken cancellation)
    {
        var output = new StringBuilder();
        var buffer = new char[1024];
        bool overflow = false;
        try
        {
            int count;
            while ((count = await reader.ReadAsync(buffer.AsMemory(), cancellation).ConfigureAwait(false)) != 0)
            {
                if (capture)
                {
                    if (output.Length + count <= 4096) output.Append(buffer, 0, count);
                    else overflow = true;
                }
                Array.Clear(buffer);
            }
            return overflow ? "" : output.ToString();
        }
        finally { Array.Clear(buffer); output.Clear(); }
    }
}
