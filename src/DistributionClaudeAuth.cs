namespace UsageMonitor;

// Lightweight authentication seam for parser-only tests; excluded from the application.
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
