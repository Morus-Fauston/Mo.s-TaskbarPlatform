namespace Mtp.Host.Experiments;

public sealed record ProbeDisplayDecision(string DisplayId, bool Show, bool UserWantsVisible, string Reason);

/// <summary>
/// Host experiment boundary. This only computes temporary suppression and never writes preferences
/// or mutates component data. The observation program does not connect it to product windows.
/// </summary>
public sealed class ProbeDisplayGate
{
    public ProbeDisplayDecision Update(string displayId, ProbeExposure exposure, bool userWantsVisible,
        bool signalValidatedForExperiment, TimeSpan sampleAge)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(displayId);
        var reason = !userWantsVisible ? "user_hidden" : !signalValidatedForExperiment ? "observation_only" :
            sampleAge < TimeSpan.Zero || sampleAge > TimeSpan.FromMilliseconds(100) ? "stale_or_invalid_sample" :
            exposure != ProbeExposure.VisibleCandidate ? "taskbar_hidden_or_unknown" : "validated_experiment_candidate";
        return new(displayId, reason == "validated_experiment_candidate", userWantsVisible, reason);
    }
}
