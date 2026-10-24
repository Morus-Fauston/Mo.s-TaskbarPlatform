namespace Mtp.Host;

/// <summary>Projects right-relative animation coordinates into the integer-sized native viewport.</summary>
public static class TaskbarFrameProjection
{
    public static double ViewportWidth(double animatedWidthDip, double rasterizationScale)
    {
        if (!double.IsFinite(animatedWidthDip) || animatedWidthDip < 0)
            throw new ArgumentOutOfRangeException(nameof(animatedWidthDip));
        if (!double.IsFinite(rasterizationScale) || rasterizationScale <= 0)
            throw new ArgumentOutOfRangeException(nameof(rasterizationScale));
        // The native host fixes its right edge using an integer pixel width. Using
        // the unrounded width again in Canvas introduces a second, independent
        // rounding origin, which can reverse a slowly moving item by one pixel.
        return animatedWidthDip == 0 ? 0 : Math.Max(1,
            Math.Round(animatedWidthDip * rasterizationScale, MidpointRounding.AwayFromZero)) / rasterizationScale;
    }
}
