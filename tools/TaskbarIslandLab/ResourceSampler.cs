using System.Diagnostics;
using TaskbarIslandLab.Logic;

namespace TaskbarIslandLab;

internal sealed record ResourceSnapshot(double WallSeconds, double CpuSeconds, long PrivateBytes, long WorkingSetBytes, int Threads, int Handles, long Requested, long Actual);

internal sealed class ResourceSampler : IDisposable
{
    private readonly Process process = Process.GetCurrentProcess();
    private readonly Stopwatch clock = Stopwatch.StartNew();
    public double SamplingWallMilliseconds { get; private set; }
    public ResourceSnapshot Read(long requested, long actual)
    {
        var begin = Stopwatch.GetTimestamp();
        process.Refresh();
        var snapshot = new ResourceSnapshot(clock.Elapsed.TotalSeconds, process.TotalProcessorTime.TotalSeconds,
            process.PrivateMemorySize64, process.WorkingSet64, process.Threads.Count, process.HandleCount, requested, actual);
        SamplingWallMilliseconds += Stopwatch.GetElapsedTime(begin).TotalMilliseconds;
        return snapshot;
    }
    public static object Difference(ResourceSnapshot first, ResourceSnapshot last) => new
    {
        durationSeconds = last.WallSeconds - first.WallSeconds,
        requestedUpdates = last.Requested - first.Requested,
        actualContentUpdates = last.Actual - first.Actual,
        cpu = CpuCost.Calculate(last.CpuSeconds - first.CpuSeconds, Math.Max(0.000001, last.WallSeconds - first.WallSeconds), Environment.ProcessorCount, last.Actual - first.Actual),
        privateBytesDelta = last.PrivateBytes - first.PrivateBytes,
        workingSetBytesDelta = last.WorkingSetBytes - first.WorkingSetBytes,
        threadDelta = last.Threads - first.Threads,
        handleDelta = last.Handles - first.Handles,
        first,
        last
    };
    public void Dispose() => process.Dispose();
}
