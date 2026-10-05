using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Mtp.Platform.Core;

namespace Mtp.Host;

/// <summary>Commits independent Host preferences without replacing unreadable state with defaults.</summary>
public sealed class LocalHostSettingsPreferenceStore(string path) : IHostSettingsPreferenceStore
{
    public const int MaximumBytes = 1024 * 1024;
    public const int MaximumIdentities = 4096;
    private static readonly JsonSerializerOptions Options = new()
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        WriteIndented = true,
        MaxDepth = 8
    };

    public CoreResult<HostSettingsPreferences> Load()
    {
        try
        {
            if (!BoundedUtf8File.TryRead(path, MaximumBytes, out var json))
                return Failure("settings_invalid", "设置文件超过 1 MiB，保留原文件。");
            return Validate(JsonSerializer.Deserialize<HostSettingsPreferences>(json, Options));
        }
        catch (FileNotFoundException) { return CoreResult<HostSettingsPreferences>.Success(new(new(), Array.Empty<string>())); }
        catch (DecoderFallbackException) { return Failure("settings_invalid", "设置文件包含非法 UTF-8，保留原文件。"); }
        catch (JsonException) { return Failure("settings_invalid", "设置格式或字段无效，保留原文件。"); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        { return Failure("settings_read_failed", "无法读取设置，保留原文件。"); }
    }

    public CoreResult<HostSettingsPreferences> CommitAppearance(HostAppearancePreferences appearance) =>
        Commit(current => current with { Appearance = appearance });

    public CoreResult<HostSettingsPreferences> CommitOrder(IReadOnlyList<string> order) =>
        Commit(current => current with { ComponentOrder = order });

    private CoreResult<HostSettingsPreferences> Commit(Func<HostSettingsPreferences, HostSettingsPreferences> change)
    {
        string? temporary = null;
        try
        {
            var fullPath = Path.GetFullPath(path);
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            using var fileLock = PreferenceFileLock.Acquire(fullPath, TimeSpan.FromSeconds(2));
            var loaded = Load();
            if (!loaded.IsSuccess) return loaded;
            var next = Validate(change(loaded.Value!));
            if (!next.IsSuccess) return next;
            var bytes = JsonSerializer.SerializeToUtf8Bytes(next.Value!, Options);
            if (bytes.Length > MaximumBytes) return Failure("settings_too_large", "设置超过 1 MiB，保留原值。");
            temporary = fullPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            if (File.Exists(fullPath)) File.Replace(temporary, fullPath, null);
            else File.Move(temporary, fullPath);
            return next;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        { return Failure("settings_write_failed", "设置保存失败，保留上次完整值。"); }
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

    internal static CoreResult<HostSettingsPreferences> Validate(HostSettingsPreferences? preferences)
    {
        if (preferences?.Appearance is not { } appearance || !Enum.IsDefined(appearance.Theme) || !Enum.IsDefined(appearance.Material) ||
            !double.IsFinite(appearance.Opacity) || appearance.Opacity is < 0 or > 1 ||
            preferences.ComponentOrder is null || preferences.ComponentOrder.Count > MaximumIdentities)
            return Invalid();
        var keys = new List<string>(preferences.ComponentOrder.Count);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var key in preferences.ComponentOrder)
        {
            try
            {
                if (key is null || key.Length > 8192) return Invalid();
                var segments = JsonSerializer.Deserialize<string[]>(key);
                if (segments is not { Length: 3 } || segments.Any(value => string.IsNullOrWhiteSpace(value) || value.Length > 256 || value != value.Trim()))
                    return Invalid();
                var canonical = JsonSerializer.Serialize(segments);
                if (!seen.Add(canonical)) return Invalid();
                keys.Add(canonical);
            }
            catch (JsonException) { return Invalid(); }
        }
        return CoreResult<HostSettingsPreferences>.Success(preferences with { ComponentOrder = keys.AsReadOnly() });
    }

    private static CoreResult<HostSettingsPreferences> Invalid() =>
        CoreResult<HostSettingsPreferences>.Failure(new("settings_invalid", "设置值、稳定身份或预算无效，保留原值。"));
    private CoreResult<HostSettingsPreferences> Failure(string code, string message) =>
        CoreResult<HostSettingsPreferences>.Failure(new(code, message, path));
}
