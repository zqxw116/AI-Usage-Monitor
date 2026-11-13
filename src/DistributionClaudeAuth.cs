namespace UsageMonitor;

// The distributed monitor uses the Claude Code login. It does not open Desktop profiles.
internal sealed class ClaudeDesktopCredential
{
    public string? Bearer { get; init; }
    public string? SessionKey { get; init; }
    public string? Organization { get; init; }
}

internal static class ClaudeDesktopAuth
{
    internal sealed class RenewalPendingException() : InvalidOperationException("인증 갱신 대기") { }
    public static ClaudeDesktopCredential? Read() => null;
}
