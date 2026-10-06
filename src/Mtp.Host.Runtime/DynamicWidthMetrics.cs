using Mtp.Contracts;

namespace Mtp.Host;

/// <summary>Host density tokens; content readings never participate in width measurement.</summary>
public static class DynamicWidthMetrics
{
    public static double Measure(ContentWidth width, bool compact = false, HostPresentationEnvironment? environment = null)
    {
        ArgumentNullException.ThrowIfNull(width);
        if (width.Tier.HasValue == width.Slots.HasValue)
            throw new ArgumentException("Specify exactly one width tier or slot count.", nameof(width));
        var slots = width.Tier switch
        {
            WidthTier.Small => 1,
            WidthTier.Medium => 2,
            WidthTier.Large => 3,
            null when width.Slots is >= 1 and <= DynamicContentLimits.MaximumSlots => width.Slots.Value,
            _ => throw new ArgumentOutOfRangeException(nameof(width))
        };
        var resolved = environment ?? new HostPresentationEnvironment(compact ? HostPresentationDensity.Compact : HostPresentationDensity.Normal);
        return slots * resolved.SlotWidthDip;
    }
}
