using System.Text.Json;

namespace UsageMonitor;

internal sealed class MonitorSettings
{
    public bool CodexEnabled { get; set; } = true;
    public bool ClaudeEnabled { get; set; } = true;
    public bool GeminiEnabled { get; set; }
    public bool VisualEffects { get; set; } = true;
    internal bool IsEnabled(ServiceKind kind) => kind switch
    {
        ServiceKind.Codex => CodexEnabled,
        ServiceKind.Claude => ClaudeEnabled,
        _ => GeminiEnabled
    };
    internal void SetEnabled(ServiceKind kind, bool enabled)
    {
        if (kind == ServiceKind.Codex) CodexEnabled = enabled;
        else if (kind == ServiceKind.Claude) ClaudeEnabled = enabled;
        else GeminiEnabled = enabled;
    }
    internal static string FilePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AIUsageMonitor", "settings.json");

    internal static MonitorSettings Load(string? path = null)
    {
        try
        {
            using var stream = File.OpenRead(path ?? FilePath);
            return JsonSerializer.Deserialize<MonitorSettings>(stream) ?? new();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException) { return new(); }
    }

    internal void Save(string? path = null)
    {
        path ??= FilePath;
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        string temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(this));
        File.Move(temporary, path, true);
    }
}
