using System.Runtime.InteropServices;

namespace Mtp.Host.WindowTests;

/// <summary>
/// Failure classes of the combination fixture. The point of the taxonomy is that a red run must say
/// <em>whose</em> problem it is without re-running anything: a product defect, a defect in this
/// fixture, something about the machine, or a resource ceiling. Evidence/14 spent most of its time on
/// "the round failed and nobody could tell which of the four it was", which is the defect this type
/// exists to remove.
///
/// Classification rules (the single source for both the report and the process exit code):
///
/// - <see cref="Software"/> — the fixture asked production to do something the五期 contract requires
///   and production did not do it. Examples: a live taskbar group whose template never reaches
///   <c>panel</c>; an action whose confirmed SDK state never arrives; a group that outlives
///   <c>CloseFixture</c>. These are the runs that should page someone about MTP itself.
/// - <see cref="Fixture"/> — the harness's own contract was violated: a stale window reference, an
///   action name this fixture does not declare, an impossible lifecycle snapshot of a fixture-owned
///   window, a deliberate injected defect. Never retried: evidence/14 recorded how a retry loop hid
///   the real "two flyout groups on one screen squeeze each other" defect for several batches.
/// - <see cref="Environment"/> — the machine, not the code: another application closing a flyout by
///   the documented "input outside the group closes it" rule, a service that could not restart
///   because the machine was busy, the desktop being driven while the run is in flight. The only
///   class that may be retried, and only through the bounded retry points listed in
///   <see cref="FixtureOutcome.RetryPoints"/>.
/// - <see cref="Resource"/> — a resource ceiling was crossed. Since the maintainer's 2026-10-07
///   ruling the trend is reported as "plateau + absolute values" and no longer vetoes a run, so this
///   class exists for genuine ceilings (an unbounded climb that never plateaus, a hard budget
///   overrun) rather than for the old <c>handleDelta &lt;= 256</c> gate.
/// </summary>
internal enum FailureClass
{
    None = 0,
    Software = 1,
    Fixture = 2,
    Environment = 3,
    Resource = 4
}

/// <summary>
/// The fixture's own exception type. Every failure path in the combination fixture throws this, so a
/// failure can never reach the runner as a bare <see cref="Exception"/> with an unstated class.
/// </summary>
internal sealed class FixtureFailureException : Exception
{
    public FixtureFailureException(FailureClass failureClass, string code, string message, Exception? inner = null)
        : base(message, inner)
    {
        FailureClass = failureClass;
        Code = code;
    }

    /// <summary>Classification of this failure; drives the exit code and the report.</summary>
    public FailureClass FailureClass { get; }

    /// <summary>Stable machine-readable code, e.g. <c>software.taskbar-panel-missing</c>.</summary>
    public string Code { get; }
}

/// <summary>
/// Exit-code contract of the combination fixture, and the classification helpers that produce it.
///
/// The codes are distinct on purpose so a scheduled job or a human can branch on "whose problem is
/// this" without parsing the log:
///
/// <list type="table">
/// <item><term>0</term><description>All requested rounds completed, every assertion passed, cleanup verified.</description></item>
/// <item><term>10</term><description>Software failure — production did not honour a contract.</description></item>
/// <item><term>11</term><description>Fixture failure — this harness is wrong; do not retry it.</description></item>
/// <item><term>12</term><description>Environment failure — the machine interfered; retry is allowed.</description></item>
/// <item><term>13</term><description>Resource failure — a ceiling or the run budget was crossed.</description></item>
/// <item><term>14</term><description>Unclassified failure — a bug in the classifier; treat as fixture.</description></item>
/// <item><term>2</term><description>The run never reached a verdict (harness crashed before reporting).</description></item>
/// </list>
/// </summary>
internal static class FixtureOutcome
{
    public const int ExitPassed = 0;
    public const int ExitSoftwareFailure = 10;
    public const int ExitFixtureFailure = 11;
    public const int ExitEnvironmentFailure = 12;
    public const int ExitResourceFailure = 13;
    public const int ExitUnclassified = 14;
    public const int ExitNoVerdict = 2;

    /// <summary>
    /// Every retry point in the fixture, with its class and its bound. The ticket requires this list
    /// to be enumerable so "a retry hid a defect" can be reviewed instead of discovered. All of them
    /// are bounded in wall time and none of them retries an assertion on fixture-owned state.
    /// </summary>
    public static IReadOnlyList<RetryPoint> RetryPoints { get; } =
    [
        new("taskbar-panel-restore", FailureClass.Environment, "20s",
            "Re-requests the taskbar group when a live group is absent or parked on another template. " +
            "A real desktop click outside the offscreen group closes it by contract, so absence is an " +
            "environment condition; a group that is present and never reaches \"panel\" still fails."),
        new("interactive-owner-group", FailureClass.Environment, "20s",
            "Re-requests the interactive hint's owner group when none is live. Production refuses the " +
            "hint while its owner is missing; the owner disappearing is an environment event."),
        new("event-confirm", FailureClass.Environment, "20s",
            "Re-locates and re-invokes the event confirm button. Event groups retire on production's own " +
            "idle schedule, so a button located a moment ago can belong to a group already retiring."),
        new("invoke-com-retry", FailureClass.Environment, "2s",
            "Retries the peer Invoke that returns COMException 0x80040200 (element not available). " +
            "Observed when a group is replaced between the probe and the invoke.")
    ];

    public static int ExitCode(FailureClass failureClass) => failureClass switch
    {
        FailureClass.None => ExitPassed,
        FailureClass.Software => ExitSoftwareFailure,
        FailureClass.Fixture => ExitFixtureFailure,
        FailureClass.Environment => ExitEnvironmentFailure,
        FailureClass.Resource => ExitResourceFailure,
        _ => ExitUnclassified
    };

    public static string Describe(FailureClass failureClass) => failureClass switch
    {
        FailureClass.None => "passed",
        FailureClass.Software => "software-failure",
        FailureClass.Fixture => "fixture-failure",
        FailureClass.Environment => "environment-failure",
        FailureClass.Resource => "resource-failure",
        _ => "unclassified-failure"
    };

    /// <summary>
    /// Maps an arbitrary exception onto a class. Only exceptions the fixture raised deliberately carry
    /// their own class; everything else is mapped by what it means:
    /// timeouts and cancellation mean the machine did not deliver in time (environment), while an
    /// argument/state error is this harness misusing its own objects (fixture).
    /// </summary>
    public static FailureClass Classify(Exception error) => error switch
    {
        FixtureFailureException failure => failure.FailureClass,
        TimeoutException => FailureClass.Environment,
        OperationCanceledException => FailureClass.Resource,
        COMException => FailureClass.Environment,
        ArgumentException => FailureClass.Fixture,
        InvalidOperationException => FailureClass.Fixture,
        KeyNotFoundException => FailureClass.Fixture,
        NullReferenceException => FailureClass.Fixture,
        _ => FailureClass.Fixture
    };

    public static string CodeOf(Exception error) => error switch
    {
        FixtureFailureException failure => failure.Code,
        TimeoutException => "environment.timeout",
        OperationCanceledException => "resource.budget-exceeded",
        COMException => "environment.com",
        _ => "fixture.unclassified"
    };

    public static int ExitCodeOf(Exception error) => ExitCode(Classify(error));

    public sealed record RetryPoint(string Name, FailureClass Class, string Bound, string Rationale);
}
