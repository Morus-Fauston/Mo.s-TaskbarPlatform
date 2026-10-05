using System;
using System.Collections.Generic;
using System.Linq;

namespace Mtp.Platform.Core;

public sealed record TaskbarComponentKey(string ApplicationId, string FeatureGroupId, string ComponentId);
public sealed record TaskbarItemKey(TaskbarComponentKey Component, string ItemId, long PresenceGeneration);
public readonly record struct TaskbarDipRect(double X, double Y, double Width, double Height)
{
    public double Right => X + Width;
    public double Bottom => Y + Height;
}
public sealed record TaskbarMeasuredItem(TaskbarItemKey Key, double WidthDip, bool IsInteractive = true);
public sealed record TaskbarMeasuredComponent(TaskbarComponentKey Key, double FixedWidthDip = 0,
    IReadOnlyList<TaskbarMeasuredItem>? Items = null);
public sealed record TaskbarComponentPlacement(TaskbarComponentKey Key, TaskbarDipRect Bounds);
public sealed record TaskbarItemPlacement(TaskbarItemKey Key, TaskbarDipRect Bounds, bool IsInteractive);

/// <summary>Immutable geometry relative to the group's fixed right edge (X = 0).</summary>
public sealed class TaskbarGroupLayoutResult
{
    internal TaskbarGroupLayoutResult(double width, double height,
        IReadOnlyList<TaskbarComponentPlacement> components, IReadOnlyList<TaskbarItemPlacement> items, bool overflows)
        => (WidthDip, HeightDip, Components, Items, Overflows) = (width, height, components, items, overflows);
    public double WidthDip { get; }
    public double HeightDip { get; }
    public IReadOnlyList<TaskbarComponentPlacement> Components { get; }
    public IReadOnlyList<TaskbarItemPlacement> Items { get; }
    public bool Overflows { get; }
}

public static class TaskbarGroupLayout
{
    // The component bound covers every node accepted across all 16 application declarations.
    public const int MaximumComponents = 16 * 4096;
    public const int MaximumItems = 16 * 128;
    public const double MaximumDimensionDip = 1_000_000;
    public const double ItemGapDip = 4;
    public const double ComponentGapDip = 8;
    public const double HorizontalPaddingDip = 4;

    public static TaskbarGroupLayoutResult Calculate(IReadOnlyList<TaskbarMeasuredComponent> orderedComponents,
        double heightDip, double availableWidthDip = double.MaxValue)
    {
        ArgumentNullException.ThrowIfNull(orderedComponents);
        if (!ValidDimension(heightDip) || !double.IsFinite(availableWidthDip) || availableWidthDip < 0)
            throw new ArgumentOutOfRangeException(nameof(heightDip), "Height and available width must be finite and valid.");
        if (orderedComponents.Count > MaximumComponents)
            throw new ArgumentOutOfRangeException(nameof(orderedComponents));
        var components = new List<(TaskbarComponentKey Key, double Width, TaskbarMeasuredItem[] Items)>();
        var componentKeys = new HashSet<TaskbarComponentKey>();
        var itemKeys = new HashSet<TaskbarItemKey>();
        var stableItems = new HashSet<(TaskbarComponentKey Component, string ItemId)>();
        foreach (var component in orderedComponents)
        {
            if (component is null || !ValidKey(component.Key) || !componentKeys.Add(component.Key) ||
                !double.IsFinite(component.FixedWidthDip) || component.FixedWidthDip < 0 ||
                component.FixedWidthDip > MaximumDimensionDip ||
                (component.Items is not null && component.FixedWidthDip != 0))
                throw new ArgumentException("Invalid, duplicate or ambiguous component measurement.", nameof(orderedComponents));
            if (component.Items is { Count: > MaximumItems } ||
                (component.Items?.Count ?? 0) > MaximumItems - itemKeys.Count)
                throw new ArgumentOutOfRangeException(nameof(orderedComponents), "Too many current items.");
            var items = component.Items?.ToArray() ?? Array.Empty<TaskbarMeasuredItem>();
            double width = component.FixedWidthDip;
            foreach (var item in items)
            {
                if (item is null || item.Key is null || item.Key.Component != component.Key ||
                    !ValidId(item.Key.ItemId) || item.Key.PresenceGeneration < 0 ||
                    !ValidDimension(item.WidthDip) || !itemKeys.Add(item.Key) ||
                    !stableItems.Add((item.Key.Component, item.Key.ItemId)))
                    throw new ArgumentException("Invalid or duplicate item measurement.", nameof(orderedComponents));
                width += item.WidthDip;
            }
            if (items.Length > 1) width += ItemGapDip * (items.Length - 1);
            if (width > 0) components.Add((component.Key, width, items));
        }
        if (components.Count == 0)
            return new(0, heightDip, Array.Empty<TaskbarComponentPlacement>(), Array.Empty<TaskbarItemPlacement>(), false);
        var widthDip = components.Sum(x => x.Width) + (components.Count - 1) * ComponentGapDip + 2 * HorizontalPaddingDip;
        var componentPlacements = new TaskbarComponentPlacement[components.Count];
        var itemPlacements = new List<TaskbarItemPlacement>(itemKeys.Count);
        // Walking from the anchored right makes neighbors on the right independent of all left widths.
        double right = -HorizontalPaddingDip;
        for (var index = components.Count - 1; index >= 0; index--)
        {
            var component = components[index];
            componentPlacements[index] = new(component.Key, new(right - component.Width, 0, component.Width, heightDip));
            right -= component.Width + ComponentGapDip;
        }
        for (var index = 0; index < components.Count; index++)
        {
            double x = componentPlacements[index].Bounds.X;
            foreach (var item in components[index].Items)
            {
                itemPlacements.Add(new(item.Key, new(x, 0, item.WidthDip, heightDip), item.IsInteractive));
                x += item.WidthDip + ItemGapDip;
            }
        }
        return new(widthDip, heightDip, Array.AsReadOnly(componentPlacements), itemPlacements.AsReadOnly(), widthDip > availableWidthDip);
    }

    private static bool ValidDimension(double value) => double.IsFinite(value) && value > 0 && value <= MaximumDimensionDip;
    private static bool ValidId(string? value) => !string.IsNullOrWhiteSpace(value) && value.Length <= 256;
    private static bool ValidKey(TaskbarComponentKey? key) => key is not null &&
        ValidId(key.ApplicationId) && ValidId(key.FeatureGroupId) && ValidId(key.ComponentId);
}
