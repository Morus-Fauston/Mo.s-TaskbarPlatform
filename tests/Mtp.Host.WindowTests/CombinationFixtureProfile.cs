using System.Globalization;

namespace Mtp.Host.WindowTests;

/// <summary>
/// Run profile of the combination fixture — the "tiered entry" the ticket asks for. Both tiers drive
/// the same fixture, the same assertions and the same error classes; only the round count, the budget
/// and the fault-injection schedule differ. Choosing a profile therefore never changes <em>what</em>
/// is verified, only how much of it accumulates.
///
/// <list type="table">
/// <item><term>smoke</term><description>Default. 3 rounds, ~1 minute. Per-round coverage only.</description></item>
/// <item><term>soak</term><description>50 rounds, ~5-6 minutes. Adds trend, faults and accumulation. One per major version.</description></item>
/// </list>
///
/// What smoke covers: content island load, theme switch, business item composition, per-component
/// action and template invocations, taskbar group request and panel navigation (panel → child → back),
/// short hint, interactive hint (owner group, slider confirmation, expansion), event flyout creation
/// and confirmation, reverse expansion, timer pause/resume and batch expansion, organization
/// merge/split, plus the one-shot teardown assertions.
///
/// What smoke cannot cover, by construction: resource trend (one round has no slope), the fault
/// schedule (14票 injected at rounds 10/20/30/40), and accumulation effects. Those stay with soak —
/// which is why the ticket keeps both tiers instead of replacing soak with a single round.
/// </summary>
internal sealed class CombinationProfile
{
    private CombinationProfile(string name, int rounds, TimeSpan budget, IReadOnlyDictionary<int, string> faults, TimeSpan? perRoundWindowWait)
    {
        Name = name;
        Rounds = rounds;
        Budget = budget;
        Faults = faults;
        PerRoundWindowWait = perRoundWindowWait;
    }

    /// <summary>Profile name as recorded in <c>policy.json</c> and the result summary.</summary>
    public string Name { get; }

    /// <summary>Number of business rounds to run; the run must complete all of them.</summary>
    public int Rounds { get; }

    /// <summary>Hard wall-clock budget. Exceeding it is a <see cref="FailureClass.Resource"/> failure.</summary>
    public TimeSpan Budget { get; }

    /// <summary>Round number → fault name, for the fault-injection schedule.</summary>
    public IReadOnlyDictionary<int, string> Faults { get; }

    /// <summary>
    /// Explicit pause after a window is requested, before the round proceeds to assert on it. This is
    /// the "每轮时间可以放宽" requirement made concrete: the fixture waits for production's own
    /// open/close animation instead of racing it. Rounds may take longer as a result; the ticket
    /// explicitly prefers that over squeezing the clock.
    /// </summary>
    public TimeSpan? PerRoundWindowWait { get; }

    public bool IsSoak => Rounds >= 50;

    public static CombinationProfile Smoke { get; } = new(
        "smoke", 3, TimeSpan.FromMinutes(3), new Dictionary<int, string>(), TimeSpan.FromMilliseconds(300));

    public static CombinationProfile Soak { get; } = new(
        "soak", 50, TimeSpan.FromMinutes(10),
        new Dictionary<int, string>
        {
            [10] = "broker-reconnect",
            [20] = "parent-recreate",
            [30] = "service-recovery",
            [40] = "reduced-motion-switch"
        },
        TimeSpan.FromMilliseconds(300));

    /// <summary>
    /// Resolves the profile from an explicit name. An unknown name is a fixture error (typo in the
    /// runner), not something to silently fall back from.
    /// </summary>
    public static CombinationProfile Parse(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        null or "" or "smoke" => Smoke,
        "soak" or "long" or "full" => Soak,
        _ => throw new FixtureFailureException(FailureClass.Fixture, "fixture.unknown-profile",
            $"unknown combination profile '{value}'; expected 'smoke' or 'soak'")
    };

    /// <summary>Human/机器可读的一句话说明，写进 policy.json，避免报告被误读成另一种层级。</summary>
    public string Describe() => string.Create(CultureInfo.InvariantCulture,
        $"{Name}: {Rounds} rounds, budget {Budget.TotalSeconds:0}s, {Faults.Count} injected faults, " +
        $"per-window wait {(PerRoundWindowWait is null ? "none" : PerRoundWindowWait.Value.TotalMilliseconds.ToString("0", CultureInfo.InvariantCulture) + "ms")}");
}
