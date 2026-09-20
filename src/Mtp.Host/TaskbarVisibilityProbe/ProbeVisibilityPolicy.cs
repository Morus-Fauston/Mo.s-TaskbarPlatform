namespace Mtp.Host.Experiments;

public enum ProbeExposure { Unknown, Hidden, VisibleCandidate }
public enum ProbeClip { Empty, Partial, Full }

/// <summary>Experimental Windows observations, never a public platform contract.</summary>
public sealed record ProbeSignals(
    bool TaskbarExists, bool ProbeExists, bool ParentMatches, bool Stable,
    bool AttributesValid, bool TaskbarVisible, bool ProbeVisible,
    bool? FullyOnScreen, ProbeClip? Clip);

public static class ProbeVisibilityPolicy
{
    // Full GDI exposure is only a candidate: DWM/Explorer behaviour still needs human validation.
    public static ProbeExposure Evaluate(ProbeSignals signals)
    {
        if (!signals.TaskbarExists || !signals.ProbeExists || !signals.ParentMatches ||
            !signals.Stable || !signals.AttributesValid)
            return ProbeExposure.Unknown;
        if (signals.TaskbarVisible != signals.ProbeVisible) return ProbeExposure.Unknown;
        if (!signals.TaskbarVisible || signals.FullyOnScreen == false)
            return ProbeExposure.Hidden;
        if (signals.FullyOnScreen is null || signals.Clip is null) return ProbeExposure.Unknown;
        return signals.Clip == ProbeClip.Full ? ProbeExposure.VisibleCandidate : ProbeExposure.Hidden;
    }
}
