namespace Mtp.Host;

public enum HostPresentationDensity { Normal, Compact }

/// <summary>Validated display inputs shared by Host layout and native rendering.</summary>
public sealed record HostPresentationEnvironment
{
    public HostPresentationDensity Density { get; }
    public double TextScale { get; }

    public HostPresentationEnvironment(HostPresentationDensity density = HostPresentationDensity.Normal, double textScale = 1d)
    {
        if (!Enum.IsDefined(density)) throw new ArgumentOutOfRangeException(nameof(density));
        if (double.IsNaN(textScale) || double.IsInfinity(textScale) || textScale < 1d || textScale > 2.25d)
            throw new ArgumentOutOfRangeException(nameof(textScale), "Text scale must be between 1 and 2.25.");
        Density = density;
        TextScale = textScale;
    }

    public double SlotWidthDip => (Density == HostPresentationDensity.Compact ? 28d : 32d) * TextScale;

    public static HostPresentationEnvironment FromTaskbar(double heightDip, double textScale = 1d)
    {
        if (double.IsNaN(heightDip) || double.IsInfinity(heightDip) || heightDip < 32d)
            throw new ArgumentOutOfRangeException(nameof(heightDip));
        return new(heightDip < 40d ? HostPresentationDensity.Compact : HostPresentationDensity.Normal, textScale);
    }
}
