using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace UsageMonitor;

internal sealed class OfficialLogin : IDisposable
{
    internal sealed class LoginCommand(string fileName, IReadOnlyList<string> arguments)
    {
        public string FileName { get; } = fileName;
        public IReadOnlyList<string> Arguments { get; } = arguments;
    }

    private readonly object gate = new();
    private Process? running;
    private CancellationTokenSource? runningCancellation;
    private bool disposed;

    public static bool IsAvailable(bool claude) => FindCommand(claude) is not null;
    public static bool IsAvailable(ServiceKind kind) => kind == ServiceKind.Gemini ? FindGeminiCommand() is not null : IsAvailable(kind == ServiceKind.Claude);

    public static Uri InstallationUrl(ServiceKind kind) => kind == ServiceKind.Gemini
        ? new Uri("https://geminicli.com/docs/get-started/installation/") : InstallationUrl(kind == ServiceKind.Claude);

    public static Uri InstallationUrl(bool claude) => new(claude
        ? "https://code.claude.com/docs/en/setup"
        : "https://developers.openai.com/codex/cli/");

    internal static LoginCommand? FindCommand(bool claude) => FindCommand(claude,
        (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator),
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData));

    private static LoginCommand? FindGeminiCommand()
    {
        var paths = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator)
            .Select(CanonicalDirectory).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var node = paths.Select(path => Path.Combine(path, "node.exe")).FirstOrDefault(File.Exists);
        if (node is null) return null;
        var npmPaths = paths.Append(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "npm"));
        foreach (var path in npmPaths)
        {
            string[] roots = {
                Path.Combine(path, "node_modules", "@google", "gemini-cli", "node_modules", "@google", "gemini-cli-core"),
                Path.Combine(path, "node_modules", "@google", "gemini-cli-core")
            };
            foreach (var root in roots)
            foreach (var oauth in new[] { Path.Combine(root, "dist", "code_assist", "oauth2.js"), Path.Combine(root, "dist", "src", "code_assist", "oauth2.js") })
            {
                if (!File.Exists(oauth)) continue;
                // Use the installed official authentication module only; never start a model/chat session.
                const string script = "try { const { getOauthClient } = await import(process.argv[1]); await getOauthClient('oauth-personal', { getProxy: () => undefined, isBrowserLaunchSuppressed: () => false, isInteractive: () => true, getAcpMode: () => true }); process.stdin.destroy(); process.exitCode = 0; } catch { process.stdin.destroy(); process.exitCode = 1; }";
                return new(node, new[] { "--input-type=module", "--eval", script, new Uri(oauth).AbsoluteUri });
            }
        }
        return null;
    }

    // Uses only known native/script entry points; never executes a .cmd/.ps1 shim.
    internal static LoginCommand? FindCommand(bool claude, IEnumerable<string> pathDirectories, string home, string roaming)
    {
        string[] paths = pathDirectories.Select(CanonicalDirectory).OfType<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        string name = claude ? "claude" : "codex";
        string[] login = claude ? ["auth", "login"] : ["-c", "cli_auth_credentials_store=\"file\"", "login"];
        foreach (string directory in paths)
        {
            string candidate = Path.Combine(directory, name + ".exe");
            if (File.Exists(candidate)) return new(candidate, login);
        }
        if (claude)
        {
            string candidate = Path.Combine(home, ".local", "bin", "claude.exe");
            if (File.Exists(candidate)) return new(candidate, login);
        }
        string[] npmDirectories = paths.Append(Path.Combine(roaming, "npm"))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        string[] architectures = RuntimeInformation.OSArchitecture == Architecture.Arm64 ? ["arm64", "x64"] : ["x64", "arm64"];
        foreach (string npm in npmDirectories)
        {
            string root = Path.Combine(npm, "node_modules", claude ? "@anthropic-ai" : "@openai", claude ? "claude-code" : "codex");
            if (claude)
            {
                string native = Path.Combine(root, "bin", "claude.exe");
                if (File.Exists(native)) return new(native, login);
            }
            foreach (string architecture in architectures)
            {
                string package = (claude ? "claude-code-win32-" : "codex-win32-") + architecture;
                string target = architecture == "arm64" ? "aarch64-pc-windows-msvc" : "x86_64-pc-windows-msvc";
                string[] candidates = claude
                    ? [Path.Combine(root, "node_modules", "@anthropic-ai", package, "claude.exe"),
                       Path.Combine(npm, "node_modules", "@anthropic-ai", package, "claude.exe")]
                    : [Path.Combine(root, "node_modules", "@openai", package, "vendor", target, "bin", "codex.exe"),
                       Path.Combine(root, "node_modules", "@openai", package, "vendor", target, "codex", "codex.exe"),
                       Path.Combine(npm, "node_modules", "@openai", package, "vendor", target, "bin", "codex.exe"),
                       Path.Combine(root, "vendor", target, "bin", "codex.exe"),
                       Path.Combine(root, "vendor", target, "codex", "codex.exe")];
                foreach (string candidate in candidates)
                    if (File.Exists(candidate)) return new(candidate, login);
            }
        }
        string? node = paths.Select(directory => Path.Combine(directory, "node.exe")).FirstOrDefault(File.Exists);
        if (node is null) return null;
        foreach (string npm in npmDirectories)
        {
            string script = claude
                ? Path.Combine(npm, "node_modules", "@anthropic-ai", "claude-code", "cli.js")
                : Path.Combine(npm, "node_modules", "@openai", "codex", "bin", "codex.js");
            if (File.Exists(script)) return new(node, new[] { script }.Concat(login).ToArray());
        }
        return null;
    }

    private static string? CanonicalDirectory(string path)
    {
        try
        {
            path = path.Trim().Trim('"');
            return Path.IsPathFullyQualified(path) ? Path.GetFullPath(path) : null;
        }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or IOException) { return null; }
    }

    internal static ProcessStartInfo CreateStartInfo(LoginCommand command, string home)
    {
        var info = new ProcessStartInfo(command.FileName)
        {
            WorkingDirectory = home,
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (string argument in command.Arguments) info.ArgumentList.Add(argument);
        return info;
    }

    public Task<bool> RunAsync(bool claude, Action<Uri>? onLoginUrl, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        var command = FindCommand(claude) ?? throw new InvalidOperationException(
            (claude ? "Claude Code" : "Codex CLI") + "가 설치되어 있지 않습니다. 공식 설치 안내: " + InstallationUrl(claude));
        return RunCommandAsync(command, onLoginUrl, cancellation);
    }

    public Task<bool> RunAsync(ServiceKind kind, Action<Uri>? onLoginUrl, CancellationToken cancellation)
    {
        if (kind != ServiceKind.Gemini) return RunAsync(kind == ServiceKind.Claude, onLoginUrl, cancellation);
        cancellation.ThrowIfCancellationRequested();
        var command = FindGeminiCommand() ?? throw new InvalidOperationException("Gemini CLI의 Node.js 로그인 모듈을 찾지 못했습니다. 공식 설치 안내를 확인해 주세요.");
        return RunCommandAsync(command, onLoginUrl, cancellation);
    }

    internal async Task<bool> RunCommandAsync(LoginCommand command, Action<Uri>? onLoginUrl, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        operation.CancelAfter(TimeSpan.FromMinutes(5));
        using var process = new Process { StartInfo = CreateStartInfo(command,
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)) };
        bool started = false;
        Task stdout = Task.CompletedTask, stderr = Task.CompletedTask;
        try
        {
            lock (gate)
            {
                ObjectDisposedException.ThrowIf(disposed, this);
                if (runningCancellation is not null) throw new InvalidOperationException("이미 공식 로그인을 진행 중입니다.");
                cancellation.ThrowIfCancellationRequested();
                runningCancellation = operation;
                running = process;
                started = process.Start();
                if (!started) throw new InvalidOperationException("공식 로그인 프로그램을 시작할 수 없습니다.");
            }
            using var registration = operation.Token.Register(() => StopOwnedProcess(process));
            // Leave stdin open and untouched until the login process exits.
            var observed = new HashSet<string>(StringComparer.Ordinal);
            object callbackGate = new();
            void Observe(Uri uri)
            {
                lock (callbackGate)
                {
                    if (operation.IsCancellationRequested || !observed.Add(uri.AbsoluteUri)) return;
                    try { onLoginUrl?.Invoke(uri); }
                    catch { /* Callback failures must not expose CLI output or interrupt pipe draining. */ }
                }
            }
            stdout = DrainAsync(process.StandardOutput, Observe, operation.Token);
            stderr = DrainAsync(process.StandardError, Observe, operation.Token);
            await process.WaitForExitAsync(operation.Token).ConfigureAwait(false);
            await Task.WhenAll(stdout, stderr).ConfigureAwait(false);
            operation.Token.ThrowIfCancellationRequested();
            return process.ExitCode == 0;
        }
        catch (OperationCanceledException) when (operation.IsCancellationRequested)
        {
            StopOwnedProcess(process);
            await Task.WhenAll(stdout, stderr).ConfigureAwait(false);
            if (cancellation.IsCancellationRequested || IsDisposed()) throw new OperationCanceledException(operation.Token);
            throw new InvalidOperationException("공식 로그인 제한 시간 5분이 지났습니다. 다시 연결하세요.");
        }
        catch (Exception error) when (error is Win32Exception or IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException("공식 로그인 프로그램을 실행할 수 없습니다. 설치 상태를 확인하세요.");
        }
        finally
        {
            operation.Cancel();
            if (started)
            {
                StopOwnedProcess(process);
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                try { await process.WaitForExitAsync(cleanup.Token).ConfigureAwait(false); }
                catch (Exception error) when (error is OperationCanceledException or InvalidOperationException or Win32Exception) { }
            }
            await Task.WhenAll(stdout, stderr).ConfigureAwait(false);
            lock (gate)
            {
                if (ReferenceEquals(running, process))
                {
                    running = null;
                    runningCancellation = null;
                }
            }
        }
    }

    // Parse only complete, bounded URL tokens; never retain or return raw CLI output.
    internal static Uri? ParseLoginUrl(string text)
    {
        text = text.TrimStart('(', '[', '{').TrimEnd(')', ']', '}', ',', '.', ';');
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps ||
            !uri.IsDefaultPort || uri.UserInfo.Length != 0 || uri.Fragment.Length != 0) return null;
        if (uri.Host != "auth.openai.com" && uri.Host != "claude.ai" && uri.Host != "claude.com" && uri.Host != "console.anthropic.com") return null;
        return uri.AbsolutePath.EndsWith("/authorize", StringComparison.OrdinalIgnoreCase) ? uri : null;
    }

    private static async Task DrainAsync(StreamReader reader, Action<Uri> observe, CancellationToken cancellation)
    {
        char[] buffer = new char[1024];
        var token = new StringBuilder();
        bool oversized = false;
        int escape = 0;
        void CompleteToken()
        {
            if (!oversized && ParseLoginUrl(token.ToString()) is { } uri) observe(uri);
            token.Clear();
            oversized = false;
        }
        try
        {
            int read;
            while ((read = await reader.ReadAsync(buffer.AsMemory(), cancellation).ConfigureAwait(false)) > 0)
            {
                for (int i = 0; i < read; i++)
                {
                    char character = buffer[i];
                    if (escape == 1) { escape = character == '[' ? 2 : character == ']' ? 3 : 0; continue; }
                    if (escape == 2) { if (character is >= '@' and <= '~') escape = 0; continue; }
                    if (escape == 3) { if (character == '\a') escape = 0; else if (character == '\x1b') escape = 4; continue; }
                    if (escape == 4) { escape = character == '\\' ? 0 : 3; continue; }
                    if (character == '\x1b') { CompleteToken(); escape = 1; continue; }
                    if (char.IsWhiteSpace(character) || char.IsControl(character) || character is '"' or '\'' or '<' or '>') CompleteToken();
                    else if (!oversized)
                    {
                        if (token.Length < 16384) token.Append(character);
                        else { oversized = true; token.Clear(); }
                    }
                }
                Array.Clear(buffer);
            }
            CompleteToken();
        }
        catch (Exception error) when (error is OperationCanceledException or IOException or ObjectDisposedException) { }
        finally { Array.Clear(buffer); token.Clear(); }
    }

    private bool IsDisposed() { lock (gate) return disposed; }

    private static void StopOwnedProcess(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (Exception error) when (error is InvalidOperationException or Win32Exception or NotSupportedException) { }
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
            runningCancellation?.Cancel();
            if (running is not null) StopOwnedProcess(running);
        }
    }
}
