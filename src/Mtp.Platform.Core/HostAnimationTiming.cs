using System;

namespace Mtp.Platform.Core;

public static class HostAnimationTiming
{
    public static readonly TimeSpan Duration = TimeSpan.FromMilliseconds(220);
    public static double EaseOutCubic(double progress)
    {
        var value = Math.Clamp(progress, 0, 1);
        return 1 - Math.Pow(1 - value, 3);
    }
}
