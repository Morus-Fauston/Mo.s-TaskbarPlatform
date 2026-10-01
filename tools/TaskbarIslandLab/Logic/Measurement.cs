namespace TaskbarIslandLab.Logic;

public sealed record CpuCost(double SingleCorePercent, double MachinePercent, double? MillisecondsPerUpdate)
{
    public static CpuCost Calculate(double cpuSeconds, double wallSeconds, int processors, long updates)
    {
        if (cpuSeconds < 0 || wallSeconds <= 0 || processors < 1 || updates < 0) throw new ArgumentOutOfRangeException(nameof(wallSeconds));
        return new(cpuSeconds / wallSeconds * 100, cpuSeconds / wallSeconds * 100 / processors, updates == 0 ? null : cpuSeconds * 1000 / updates);
    }
}

public sealed record MeasurementPhase(string Name, bool Visible, bool Update)
{
    public double Duration(LabOptions options) => Name switch
    {
        "warmup" => 5,
        "idle" or "restored" => 10,
        "visible" => options.VisibleSeconds,
        "hidden-data-updating" => options.HiddenSeconds,
        _ => 0
    };

    public static MeasurementPhase At(double elapsed, LabOptions options) => elapsed switch
    {
        < 5 => new("warmup", true, true),
        < 15 => new("idle", true, false),
        _ when elapsed < 15 + options.VisibleSeconds => new("visible", true, true),
        _ when elapsed < 15 + options.VisibleSeconds + options.HiddenSeconds => new("hidden-data-updating", false, true),
        _ when elapsed < options.MeasureSeconds => new("restored", true, true),
        _ => new("finished", false, false)
    };
}
