using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using Mtp.Platform.Core;

namespace Mtp.Host;

/// <summary>
/// Host-owned visibility preferences keyed by a component's complete stable identity.
/// </summary>
public sealed class ComponentDisplayPreferences
{
    private readonly Dictionary<string, bool> visibilityByIdentity;

    public ComponentDisplayPreferences()
        : this(Array.Empty<ComponentDisplayPreferenceEntry>())
    {
    }

    internal ComponentDisplayPreferences(IEnumerable<ComponentDisplayPreferenceEntry> entries)
    {
        visibilityByIdentity = new Dictionary<string, bool>(StringComparer.Ordinal);
        foreach (var entry in entries)
        {
            var key = IdentityKey(entry.IdentitySegments);
            if (!visibilityByIdentity.TryAdd(key, entry.IsVisible))
            {
                throw new ArgumentException("Component visibility preference identities must be unique.", nameof(entries));
            }
        }
    }

    public bool IsVisible(StableIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        return visibilityByIdentity.TryGetValue(IdentityKey(identity), out var isVisible) && isVisible;
    }

    internal IReadOnlyList<ComponentDisplayPreferenceEntry> Entries => visibilityByIdentity
        .Select(pair => new ComponentDisplayPreferenceEntry(ParseIdentityKey(pair.Key), pair.Value))
        .ToArray();

    internal ComponentDisplayPreferences WithVisibility(StableIdentity identity, bool isVisible)
    {
        ArgumentNullException.ThrowIfNull(identity);
        var entries = Entries.ToList();
        var key = IdentityKey(identity);
        var index = entries.FindIndex(entry => IdentityKey(entry.IdentitySegments) == key);
        var replacement = new ComponentDisplayPreferenceEntry(
            identity.Segments.Select(segment => segment.Value).ToArray(),
            isVisible);
        if (index >= 0)
        {
            entries[index] = replacement;
        }
        else
        {
            entries.Add(replacement);
        }

        return new ComponentDisplayPreferences(entries);
    }

    internal static string IdentityKey(StableIdentity identity) =>
        IdentityKey(identity.Segments.Select(segment => segment.Value));

    internal static string IdentityKey(IEnumerable<string> segments) =>
        JsonSerializer.Serialize(segments);

    private static IReadOnlyList<string> ParseIdentityKey(string key) =>
        JsonSerializer.Deserialize<string[]>(key)
        ?? throw new InvalidOperationException("A component identity preference is invalid.");
}

internal sealed record ComponentDisplayPreferenceEntry(
    IReadOnlyList<string> IdentitySegments,
    bool IsVisible);

public enum ComponentDisplayPreferenceLoadState
{
    Loaded,
    Missing,
    Invalid,
    Unavailable,
}

/// <summary>
/// Result of reading preferences. Non-loaded states carry an explanatory error and safe defaults.
/// </summary>
public sealed class ComponentDisplayPreferenceLoadResult
{
    public ComponentDisplayPreferenceLoadResult(
        ComponentDisplayPreferences preferences,
        StructuredError? error,
        ComponentDisplayPreferenceLoadState state)
    {
        Preferences = preferences ?? throw new ArgumentNullException(nameof(preferences));
        if ((state == ComponentDisplayPreferenceLoadState.Loaded) != (error is null))
        {
            throw new ArgumentException("A loaded preference result cannot contain an error, and every non-loaded result must contain one.", nameof(error));
        }

        Error = error;
        State = state;
    }

    public ComponentDisplayPreferences Preferences { get; }

    public StructuredError? Error { get; }

    public ComponentDisplayPreferenceLoadState State { get; }
}

public interface IComponentDisplayPreferenceStore
{
    ComponentDisplayPreferenceLoadResult Load();

    CoreResult<ComponentDisplayPreferences> CommitVisibility(StableIdentity identity, bool isVisible);
}

/// <summary>
/// Persists Host display preferences independently from the declaration file.
/// </summary>
public sealed class LocalComponentDisplayPreferenceStore : IComponentDisplayPreferenceStore
{
    public const int MaximumJsonSizeInBytes = 1024 * 1024;

    private static readonly JsonSerializerOptions jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        WriteIndented = true,
    };

    public LocalComponentDisplayPreferenceStore(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("A preference path is required.", nameof(path));
        }

        Path = path;
    }

    public string Path { get; }

    public CoreResult<ComponentDisplayPreferences> CommitVisibility(StableIdentity identity, bool isVisible)
    {
        ArgumentNullException.ThrowIfNull(identity);

        string fullPath;
        try
        {
            fullPath = System.IO.Path.GetFullPath(Path);
        }
        catch (ArgumentException)
        {
            return PreferenceWriteFailure();
        }
        catch (NotSupportedException)
        {
            return PreferenceWriteFailure();
        }
        catch (IOException)
        {
            return PreferenceWriteFailure();
        }

        var mutexName = $"Local\\Mtp.Host.DisplayPreferences.{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(fullPath.ToUpperInvariant())))}";
        Mutex? mutex = null;
        var ownsMutex = false;
        try
        {
            mutex = new Mutex(initiallyOwned: false, mutexName);
            try
            {
                ownsMutex = mutex.WaitOne(TimeSpan.FromSeconds(5));
            }
            catch (AbandonedMutexException)
            {
                ownsMutex = true;
            }

            if (!ownsMutex)
            {
                return PreferenceWriteFailure();
            }

            var latest = Load();
            if (latest.State == ComponentDisplayPreferenceLoadState.Unavailable)
            {
                return CoreResult<ComponentDisplayPreferences>.Failure(latest.Error!);
            }

            return Save(latest.Preferences.WithVisibility(identity, isVisible));
        }
        catch (UnauthorizedAccessException)
        {
            return PreferenceWriteFailure();
        }
        catch (IOException)
        {
            return PreferenceWriteFailure();
        }
        catch (WaitHandleCannotBeOpenedException)
        {
            return PreferenceWriteFailure();
        }
        finally
        {
            if (ownsMutex)
            {
                mutex!.ReleaseMutex();
            }

            mutex?.Dispose();
        }
    }

    public ComponentDisplayPreferenceLoadResult Load()
    {
        string fullPath;
        try
        {
            fullPath = System.IO.Path.GetFullPath(Path);
        }
        catch (ArgumentException)
        {
            return UnavailablePreference();
        }
        catch (NotSupportedException)
        {
            return UnavailablePreference();
        }
        catch (IOException)
        {
            return UnavailablePreference();
        }

        if (!File.Exists(fullPath))
        {
            return new ComponentDisplayPreferenceLoadResult(
                new ComponentDisplayPreferences(),
                new StructuredError("preference_not_found", "The display preference file was not found.", Path),
                ComponentDisplayPreferenceLoadState.Missing);
        }

        try
        {
            if (!BoundedUtf8File.TryRead(fullPath, MaximumJsonSizeInBytes, out var json))
            {
                return new ComponentDisplayPreferenceLoadResult(
                    new ComponentDisplayPreferences(),
                    new StructuredError("preference_too_large", "The display preference file exceeds the 1 MiB limit.", Path),
                    ComponentDisplayPreferenceLoadState.Invalid);
            }

            var document = JsonSerializer.Deserialize<PreferenceDocument>(json, jsonOptions);
            if (document?.Components is null)
            {
                return InvalidPreference("The display preference document must contain a components list.");
            }

            var entries = new List<ComponentDisplayPreferenceEntry>(document.Components.Count);
            foreach (var component in document.Components)
            {
                if (component is null || component.Identity is null || component.Identity.Count == 0 ||
                    component.Identity.Any(string.IsNullOrWhiteSpace))
                {
                    return InvalidPreference("A display preference must contain a non-empty identity.");
                }

                foreach (var segment in component.Identity)
                {
                    _ = new StableId(segment);
                }

                entries.Add(new ComponentDisplayPreferenceEntry(component.Identity, component.IsVisible));
            }

            try
            {
                return new ComponentDisplayPreferenceLoadResult(
                    new ComponentDisplayPreferences(entries),
                    null,
                    ComponentDisplayPreferenceLoadState.Loaded);
            }
            catch (ArgumentException)
            {
                return InvalidPreference("Display preference identities must be unique.");
            }
        }
        catch (JsonException)
        {
            return InvalidPreference("The display preference JSON is invalid.");
        }
        catch (ArgumentException)
        {
            return InvalidPreference("The display preference contains an invalid stable identity.");
        }
        catch (UnauthorizedAccessException)
        {
            return new ComponentDisplayPreferenceLoadResult(
                new ComponentDisplayPreferences(),
                new StructuredError("preference_read_failed", "The display preference file cannot be read.", Path),
                ComponentDisplayPreferenceLoadState.Unavailable);
        }
        catch (IOException)
        {
            return new ComponentDisplayPreferenceLoadResult(
                new ComponentDisplayPreferences(),
                new StructuredError("preference_read_failed", "The display preference file cannot be read.", Path),
                ComponentDisplayPreferenceLoadState.Unavailable);
        }
    }

    private CoreResult<ComponentDisplayPreferences> Save(ComponentDisplayPreferences preferences)
    {
        ArgumentNullException.ThrowIfNull(preferences);

        string? temporaryPath = null;
        try
        {
            var fullPath = System.IO.Path.GetFullPath(Path);
            temporaryPath = $"{fullPath}.{Guid.NewGuid():N}.tmp";
            var document = new PreferenceDocument(
                preferences.Entries
                    .Select(entry => new PreferenceEntry(entry.IdentitySegments, entry.IsVisible))
                    .ToArray());
            var json = JsonSerializer.Serialize(document, jsonOptions);
            if (Encoding.UTF8.GetByteCount(json) > MaximumJsonSizeInBytes)
            {
                return CoreResult<ComponentDisplayPreferences>.Failure(
                    new StructuredError("preference_too_large", "The display preference exceeds the 1 MiB limit.", Path));
            }

            using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
            {
                writer.Write(json);
                writer.Flush();
                stream.Flush(flushToDisk: true);
            }

            if (File.Exists(fullPath))
            {
                File.Replace(temporaryPath, fullPath, destinationBackupFileName: null);
            }
            else
            {
                File.Move(temporaryPath, fullPath);
            }

            return CoreResult<ComponentDisplayPreferences>.Success(preferences);
        }
        catch (UnauthorizedAccessException)
        {
            return CoreResult<ComponentDisplayPreferences>.Failure(
                new StructuredError("preference_write_failed", "The display preference file cannot be written.", Path));
        }
        catch (IOException)
        {
            return CoreResult<ComponentDisplayPreferences>.Failure(
                new StructuredError("preference_write_failed", "The display preference file cannot be written.", Path));
        }
        catch (ArgumentException)
        {
            return CoreResult<ComponentDisplayPreferences>.Failure(
                new StructuredError("preference_write_failed", "The display preference file cannot be written.", Path));
        }
        catch (NotSupportedException)
        {
            return CoreResult<ComponentDisplayPreferences>.Failure(
                new StructuredError("preference_write_failed", "The display preference file cannot be written.", Path));
        }
        finally
        {
            if (temporaryPath is not null)
            {
                try
                {
                    File.Delete(temporaryPath);
                }
                catch (IOException)
                {
                    // The failed write is already reported; a future startup can ignore this unique temp file.
                }
                catch (UnauthorizedAccessException)
                {
                    // The failed write is already reported; a future startup can ignore this unique temp file.
                }
            }
        }
    }

    private ComponentDisplayPreferenceLoadResult InvalidPreference(string message) =>
        new(
            new ComponentDisplayPreferences(),
            new StructuredError("preference_invalid", message, Path),
            ComponentDisplayPreferenceLoadState.Invalid);

    private ComponentDisplayPreferenceLoadResult UnavailablePreference() =>
        new(
            new ComponentDisplayPreferences(),
            new StructuredError("preference_read_failed", "The display preference file cannot be read.", Path),
            ComponentDisplayPreferenceLoadState.Unavailable);

    private CoreResult<ComponentDisplayPreferences> PreferenceWriteFailure() =>
        CoreResult<ComponentDisplayPreferences>.Failure(
            new StructuredError("preference_write_failed", "The display preference file cannot be written.", Path));

    private sealed record PreferenceDocument(
        IReadOnlyList<PreferenceEntry>? Components);

    private sealed record PreferenceEntry(
        IReadOnlyList<string>? Identity,
        bool IsVisible);
}

/// <summary>
/// Owns the read/modify/commit lifecycle so callers cannot overwrite preferences that were never read.
/// </summary>
internal sealed class ComponentDisplayPreferenceManager
{
    private readonly IComponentDisplayPreferenceStore store;
    private ComponentDisplayPreferences preferences = new();
    private ComponentDisplayPreferenceLoadState state = ComponentDisplayPreferenceLoadState.Unavailable;
    private StructuredError? loadError;
    private bool loadAttempted;

    public ComponentDisplayPreferenceManager(IComponentDisplayPreferenceStore store)
    {
        this.store = store ?? throw new ArgumentNullException(nameof(store));
    }

    public ComponentDisplayPreferences Current => preferences;

    public ComponentDisplayPreferenceLoadResult Load()
    {
        var result = store.Load();
        preferences = result.Preferences;
        state = result.State;
        loadError = result.Error;
        loadAttempted = true;
        return result;
    }

    public CoreResult<ComponentDisplayPreferences> SetVisibility(StableIdentity identity, bool isVisible)
    {
        ArgumentNullException.ThrowIfNull(identity);

        if (!loadAttempted || state == ComponentDisplayPreferenceLoadState.Unavailable)
        {
            var refreshed = Load();
            if (refreshed.State == ComponentDisplayPreferenceLoadState.Unavailable)
            {
                return CoreResult<ComponentDisplayPreferences>.Failure(
                    loadError ?? new StructuredError("preference_read_failed", "The display preference file cannot be read."));
            }
        }

        var saveResult = store.CommitVisibility(identity, isVisible);
        if (!saveResult.IsSuccess)
        {
            return saveResult;
        }

        preferences = saveResult.Value!;
        state = ComponentDisplayPreferenceLoadState.Loaded;
        loadError = null;
        return CoreResult<ComponentDisplayPreferences>.Success(preferences);
    }
}
