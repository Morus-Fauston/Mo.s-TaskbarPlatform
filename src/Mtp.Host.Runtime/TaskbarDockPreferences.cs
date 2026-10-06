using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Mtp.Platform.Core;

namespace Mtp.Host;

public sealed record TaskbarDockPreferences(string? TargetDisplayId = null, int RightGapDip = 8)
{
    [JsonIgnore]
    public bool IsValid => RightGapDip is >= 0 and <= 64 &&
        (TargetDisplayId is null || (!string.IsNullOrWhiteSpace(TargetDisplayId) && TargetDisplayId.Length <= 256));
}

public interface ITaskbarDockPreferenceStore
{
    CoreResult<TaskbarDockPreferences> Load();
    CoreResult<TaskbarDockPreferences> CommitDisplay(string? displayId);
    CoreResult<TaskbarDockPreferences> CommitGap(int gapDip);
}

/// <summary>Atomic independent field commits; unreadable or invalid old state is never overwritten.</summary>
public sealed class LocalTaskbarDockPreferenceStore(string path) : ITaskbarDockPreferenceStore
{
    private static readonly JsonSerializerOptions options = new()
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        WriteIndented = true,
    };

    public CoreResult<TaskbarDockPreferences> Load()
    {
        try
        {
            if (!BoundedUtf8File.TryRead(path, 1024 * 1024, out var json))
                return Failure("dock_preference_invalid", "任务栏设置超过 1 MiB，未覆盖原文件。");
            var settings = JsonSerializer.Deserialize<TaskbarDockPreferences>(json, options);
            return settings is { IsValid: true }
                ? CoreResult<TaskbarDockPreferences>.Success(settings)
                : Failure("dock_preference_invalid", "任务栏设置无效，未覆盖原文件。");
        }
        catch (FileNotFoundException) { return CoreResult<TaskbarDockPreferences>.Success(new()); }
        catch (DecoderFallbackException) { return Failure("dock_preference_invalid", "任务栏设置包含非法 UTF-8，未覆盖原文件。"); }
        catch (JsonException) { return Failure("dock_preference_invalid", "任务栏设置格式无效，未覆盖原文件。"); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        { return Failure("dock_preference_read_failed", "无法读取任务栏设置，保留原文件。"); }
    }

    public CoreResult<TaskbarDockPreferences> CommitDisplay(string? displayId) => Commit(current => current with { TargetDisplayId = displayId });
    public CoreResult<TaskbarDockPreferences> CommitGap(int gapDip) => Commit(current => current with { RightGapDip = gapDip });

    private CoreResult<TaskbarDockPreferences> Commit(Func<TaskbarDockPreferences, TaskbarDockPreferences> change)
    {
        string? temporary = null;
        try
        {
            var fullPath = Path.GetFullPath(path);
            using var fileLock = PreferenceFileLock.Acquire(fullPath, TimeSpan.FromSeconds(2));
            var loaded = Load();
            if (!loaded.IsSuccess) return loaded;
            var next = change(loaded.Value!);
            if (!next.IsValid) return Failure("dock_preference_invalid", "目标屏幕无效或间距不在 0 到 64 DIP 范围内。");
            temporary = fullPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, next, options);
                stream.Flush(true);
            }
            PreferenceFileLock.Replace(temporary, fullPath);
            return CoreResult<TaskbarDockPreferences>.Success(next);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        { return Failure("dock_preference_write_failed", "任务栏设置保存失败，保留原值与当前窗口。"); }
        finally
        {
            if (temporary is not null)
            {
                try { File.Delete(temporary); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
    }

    private CoreResult<TaskbarDockPreferences> Failure(string code, string message) =>
        CoreResult<TaskbarDockPreferences>.Failure(new(code, message, path));
}
