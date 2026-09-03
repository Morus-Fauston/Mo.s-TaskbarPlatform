using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
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

    internal void Set(StableIdentity identity, bool isVisible)
    {
        ArgumentNullException.ThrowIfNull(identity);
        visibilityByIdentity[IdentityKey(identity)] = isVisible;
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

/// <summary>
/// Result of reading preferences. Invalid or missing files yield empty defaults and an explanatory error.
/// </summary>
public sealed record ComponentDisplayPreferenceLoadResult(
    ComponentDisplayPreferences Preferences,
    StructuredError? Error);

public interface IComponentDisplayPreferenceStore
{
    ComponentDisplayPreferenceLoadResult Load();

    CoreResult<ComponentDisplayPreferences> Save(ComponentDisplayPreferences preferences);
}

/// <summary>
/// Persists Host display preferences independently from the declaration file.
/// </summary>
public sealed class LocalComponentDisplayPreferenceStore : IComponentDisplayPreferenceStore
{
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

    public ComponentDisplayPreferenceLoadResult Load()
    {
        if (!File.Exists(Path))
        {
            return new ComponentDisplayPreferenceLoadResult(
                new ComponentDisplayPreferences(),
                new StructuredError("preference_not_found", "The display preference file was not found.", Path));
        }

        try
        {
            var json = File.ReadAllText(Path);
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
                    null);
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
                new StructuredError("preference_read_failed", "The display preference file cannot be read.", Path));
        }
        catch (IOException)
        {
            return new ComponentDisplayPreferenceLoadResult(
                new ComponentDisplayPreferences(),
                new StructuredError("preference_read_failed", "The display preference file cannot be read.", Path));
        }
    }

    public CoreResult<ComponentDisplayPreferences> Save(ComponentDisplayPreferences preferences)
    {
        ArgumentNullException.ThrowIfNull(preferences);

        try
        {
            var document = new PreferenceDocument(
                preferences.Entries
                    .Select(entry => new PreferenceEntry(entry.IdentitySegments, entry.IsVisible))
                    .ToArray());
            File.WriteAllText(Path, JsonSerializer.Serialize(document, jsonOptions));
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
    }

    private ComponentDisplayPreferenceLoadResult InvalidPreference(string message) =>
        new(
            new ComponentDisplayPreferences(),
            new StructuredError("preference_invalid", message, Path));

    private sealed record PreferenceDocument(
        IReadOnlyList<PreferenceEntry>? Components);

    private sealed record PreferenceEntry(
        IReadOnlyList<string>? Identity,
        bool IsVisible);
}
