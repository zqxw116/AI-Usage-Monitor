using System.Text;
using System.Text.Json;

namespace UsageMonitor;

internal sealed record UpdateNotice(Version Version, string Notes, Uri ReleasePage, bool IsRead)
{
    internal string VersionText => Version.ToString(3);
}

internal static class UpdateNoticeStore
{
    private const int MaximumNotesBytes = 16 * 1024;
    private static readonly object Gate = new();

    internal static string BoundNotes(string notes)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(notes);
        if (bytes.Length <= MaximumNotesBytes) return notes;
        int length = MaximumNotesBytes;
        while ((bytes[length] & 0xc0) == 0x80) length--;
        return Encoding.UTF8.GetString(bytes, 0, length);
    }

    internal static bool Save(UpdateRelease release, string? directory = null)
    {
        lock (Gate)
        {
            var existing = Load(directory);
            return Write(new UpdateNotice(release.Version, BoundNotes(release.ReleaseNotes), release.ReleasePage,
                existing?.Version == release.Version && existing.IsRead), directory);
        }
    }

    // Return read notices too, so the information window can still show their contents.
    internal static UpdateNotice? LoadForCurrentVersion(Version current, string? directory = null)
    {
        lock (Gate)
        {
            var notice = Load(directory);
            return notice?.Version == current ? notice : null;
        }
    }

    internal static bool Acknowledge(Version current, string? directory = null)
    {
        lock (Gate)
        {
            var notice = Load(directory);
            if (notice?.Version != current) return false;
            // The UI must keep its acknowledgement action available if this write fails.
            return notice.IsRead || Write(notice with { IsRead = true }, directory);
        }
    }

    private static string DirectoryPath(string? directory) => directory ??
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AIUsageMonitor");

    private static UpdateNotice? Load(string? directory)
    {
        try
        {
            using var stream = new FileStream(Path.Combine(DirectoryPath(directory), "update-notice.json"), FileMode.Open,
                FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (stream.Length <= 0 || stream.Length > 128 * 1024) return null;
            byte[] bytes = new byte[(int)stream.Length];
            stream.ReadExactly(bytes);
            if (stream.ReadByte() != -1) return null;
            using var document = JsonDocument.Parse(bytes);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("version", out var versionValue) || versionValue.ValueKind != JsonValueKind.String ||
                !Version.TryParse(versionValue.GetString(), out var version) || version.Build < 0 || version.Revision >= 0 ||
                !root.TryGetProperty("notes", out var notesValue) || notesValue.ValueKind != JsonValueKind.String ||
                !root.TryGetProperty("url", out var url) || url.ValueKind != JsonValueKind.String ||
                !root.TryGetProperty("read", out var read) || read.ValueKind is not JsonValueKind.True and not JsonValueKind.False) return null;
            string notes = notesValue.GetString()!;
            string expected = $"https://github.com/zqxw116/AI-Usage-Monitor/releases/tag/v{version.ToString(3)}";
            if (Encoding.UTF8.GetByteCount(notes) > MaximumNotesBytes || url.GetString() != expected) return null;
            return new UpdateNotice(version, notes, new Uri(expected), read.GetBoolean());
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or ArgumentException or NotSupportedException or System.Security.SecurityException)
        {
            return null;
        }
    }

    private static bool Write(UpdateNotice notice, string? directory)
    {
        string? temporary = null;
        try
        {
            string folder = DirectoryPath(directory);
            Directory.CreateDirectory(folder);
            temporary = Path.Combine(folder, ".update-notice-" + Guid.NewGuid().ToString("N") + ".tmp");
            byte[] json = JsonSerializer.SerializeToUtf8Bytes(new
            {
                version = notice.VersionText, notes = notice.Notes, url = notice.ReleasePage.AbsoluteUri, read = notice.IsRead
            });
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(json);
                stream.Flush(true);
            }
            File.Move(temporary, Path.Combine(folder, "update-notice.json"), true);
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or System.Security.SecurityException)
        {
            return false;
        }
        finally
        {
            if (temporary is not null) { try { File.Delete(temporary); } catch { } }
        }
    }
}
