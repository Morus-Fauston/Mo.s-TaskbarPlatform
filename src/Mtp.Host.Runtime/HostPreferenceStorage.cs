using System.IO;
using System.Text;
using Mtp.Contracts;
using Mtp.Platform.Core;

namespace Mtp.Host;

/// <summary>One per-user location, with bounded import of the existing prototype preferences.</summary>
public sealed class HostPreferenceStorage
{
    private HostPreferenceStorage(string directory)
    {
        DirectoryPath = directory;
        DisplayPath = Path.Combine(directory, "display-preferences.json");
        DockPath = Path.Combine(directory, "taskbar-dock-preferences.json");
        DisplayStore = new LocalComponentDisplayPreferenceStore(DisplayPath);
        DockStore = new LocalTaskbarDockPreferenceStore(DockPath);
        SettingsStore = new LocalHostSettingsPreferenceStore(Path.Combine(directory, "host-settings.json"));
    }

    private HostPreferenceStorage(StructuredError error)
    {
        DirectoryPath = DisplayPath = DockPath = string.Empty;
        DisplayStore = new UnavailableDisplayStore(error);
        DockStore = new UnavailableDockStore(error);
        SettingsStore = new UnavailableSettingsStore(error);
        Errors = Array.AsReadOnly(new[] { error, error });
    }

    public string DirectoryPath { get; }
    public string DisplayPath { get; }
    public string DockPath { get; }
    public IComponentDisplayPreferenceStore DisplayStore { get; private set; }
    public ITaskbarDockPreferenceStore DockStore { get; private set; }
    public IHostSettingsPreferenceStore SettingsStore { get; private set; }
    public IReadOnlyList<StructuredError> Errors { get; private set; } = [];

    public static HostPreferenceStorage Initialize(string applicationDirectory, string? localDataDirectory = null)
    {
        var root = localDataDirectory ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(root))
            return new HostPreferenceStorage(Error("LocalApplicationData", "当前用户数据目录不可用，已禁止偏好写入。"));
        HostPreferenceStorage storage;
        try
        {
            storage = new HostPreferenceStorage(Path.Combine(Path.GetFullPath(root), "Mo.s-TaskbarPlatform"));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return new HostPreferenceStorage(Error("LocalApplicationData", "当前用户数据目录无效，已禁止偏好写入。"));
        }
        var errors = new List<StructuredError>();
        var displayError = storage.Prepare(applicationDirectory, storage.DisplayPath, isDisplay: true);
        if (displayError is not null)
        {
            errors.Add(displayError);
            storage.DisplayStore = new UnavailableDisplayStore(displayError);
        }
        var dockError = storage.Prepare(applicationDirectory, storage.DockPath, isDisplay: false);
        if (dockError is not null)
        {
            errors.Add(dockError);
            storage.DockStore = new UnavailableDockStore(dockError);
        }
        storage.Errors = errors.AsReadOnly();
        return storage;
    }

    private StructuredError? Prepare(string applicationDirectory, string target, bool isDisplay)
    {
        string? temporary = null;
        try
        {
            Directory.CreateDirectory(DirectoryPath);
            using var fileLock = PreferenceFileLock.Acquire(target, TimeSpan.FromSeconds(5));
            if (File.Exists(target)) return null;
            var source = Path.Combine(applicationDirectory, Path.GetFileName(target));
            string json;
            try
            {
                if (!BoundedUtf8File.TryRead(source, LocalComponentDisplayPreferenceStore.MaximumJsonSizeInBytes, out json))
                    return Error(target, "旧偏好超过 1 MiB，未导入或覆盖。请保留原文件后处理。");
            }
            catch (FileNotFoundException) { return null; }

            // Validate the exact bytes being imported, without reading a mutable source twice.
            temporary = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var writer = new StreamWriter(stream, new UTF8Encoding(false, true)))
            {
                writer.Write(json);
                writer.Flush();
                stream.Flush(true);
            }
            var valid = isDisplay
                ? new LocalComponentDisplayPreferenceStore(temporary).Load().State == ComponentDisplayPreferenceLoadState.Loaded
                : new LocalTaskbarDockPreferenceStore(temporary).Load().IsSuccess;
            if (!valid) return Error(target, "旧偏好格式无效，未导入或覆盖。请保留原文件后处理。");
            File.Move(temporary, target, overwrite: false);
            return null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return Error(target, "无法准备或导入每用户偏好，已保留原文件并禁止本次写入。请处理权限或文件损坏后重启。");
        }
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

    private static StructuredError Error(string path, string message) => new("preference_storage_unavailable", message, path);

    private sealed class UnavailableSettingsStore(StructuredError error) : IHostSettingsPreferenceStore
    {
        public CoreResult<HostSettingsPreferences> Load() => CoreResult<HostSettingsPreferences>.Failure(error);
        public CoreResult<HostSettingsPreferences> CommitAppearance(HostAppearancePreferences appearance) => Load();
        public CoreResult<HostSettingsPreferences> CommitOrder(IReadOnlyList<string> order) => Load();
        public CoreResult<HostSettingsPreferences> CommitGrouping(StableIdentity identity, DynamicGrouping grouping) => Load();
        public CoreResult<HostSettingsPreferences> CommitHints(HostHintPreferences hints) => Load();
        public CoreResult<HostSettingsPreferences> CommitHintVisibility(StableIdentity identity, bool visible) => Load();
        public CoreResult<HostSettingsPreferences> CommitEvents(HostEventPreferences events) => Load();
        public CoreResult<HostSettingsPreferences> CommitEventVisibility(StableIdentity identity, bool visible) => Load();
    }

    private sealed class UnavailableDisplayStore(StructuredError error) : IComponentDisplayPreferenceStore
    {
        public ComponentDisplayPreferenceLoadResult Load() => new(new(), error, ComponentDisplayPreferenceLoadState.Unavailable);
        public CoreResult<ComponentDisplayPreferences> CommitVisibility(StableIdentity identity, bool isVisible) =>
            CoreResult<ComponentDisplayPreferences>.Failure(error);
    }

    private sealed class UnavailableDockStore(StructuredError error) : ITaskbarDockPreferenceStore
    {
        public CoreResult<TaskbarDockPreferences> Load() => CoreResult<TaskbarDockPreferences>.Failure(error);
        public CoreResult<TaskbarDockPreferences> CommitDisplay(string? displayId) => Load();
        public CoreResult<TaskbarDockPreferences> CommitGap(int gapDip) => Load();
    }
}
