using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Mtp.Host.Experiments;

namespace Mtp.TaskbarVisibilityLab;

internal static class Program
{
    private static readonly JsonSerializerOptions Json = new() { Converters = { new JsonStringEnumConverter() } };

    [STAThread]
    private static int Main(string[] args)
    {
        string? output = null;
        try
        {
            var options = Parse(args);
            output = options.Output;
            Directory.CreateDirectory(output);
            using var log = new StreamWriter(new FileStream(Path.Combine(output, "observations.jsonl"), FileMode.CreateNew, FileAccess.Write, FileShare.Read));
            Run(options, log);
            return 0;
        }
        catch (Exception exception)
        {
            if (output is not null)
                File.WriteAllText(Path.Combine(output, "error.txt"), exception.ToString());
            return 1;
        }
    }

    private static void Run(Options options, StreamWriter log)
    {
        using var process = Process.GetCurrentProcess();
        var started = Stopwatch.GetTimestamp();
        var cpuStart = process.TotalProcessorTime;
        var frames = 0;
        var transitions = 0;
        var lastTick = started;
        double maximumGapMs = 0;
        var last = new Dictionary<string, ProbeExposure>();
        Write(log, new
        {
            kind = "session",
            mode = options.Mode,
            utc = DateTimeOffset.UtcNow,
            qpc = started,
            frequency = Stopwatch.Frequency,
            os = RuntimeInformation.OSDescription,
            architecture = RuntimeInformation.ProcessArchitecture.ToString(),
            Environment.ProcessId,
            intervalMs = options.IntervalMs,
            seconds = options.Seconds,
            meaning = "GDI candidates; no compositor truth or human acceptance"
        });
        Fixture? fixture = null;
        Win32VisibilityProbe? probe = null;
        nuint timer = 0;
        try
        {
            if (options.Mode == "fixture") fixture = new Fixture();
            if (options.Mode != "baseline") probe = new Win32VisibilityProbe(fixture is null ? null : fixture.Targets);
            timer = Native.SetTimer(0, 0, (uint)options.IntervalMs, 0);
            if (timer == 0) throw new System.ComponentModel.Win32Exception(Marshal.GetLastPInvokeError());
            while (true)
            {
                var result = Native.GetMessageW(out var message, 0, 0, 0);
                if (result < 0) throw new System.ComponentModel.Win32Exception(Marshal.GetLastPInvokeError());
                if (result == 0) break;
                Native.TranslateMessage(ref message);
                Native.DispatchMessageW(ref message);
                if (message.Message != 0x113 || message.WParam != timer) continue;
                var tick = Stopwatch.GetTimestamp();
                maximumGapMs = Math.Max(maximumGapMs, Stopwatch.GetElapsedTime(lastTick, tick).TotalMilliseconds);
                lastTick = tick;
                fixture?.Advance(frames);
                if (probe is not null)
                {
                    var samples = probe.Capture();
                    fixture?.Verify(frames, samples);
                    Write(log, new { kind = "sample", frame = frames, qpc = tick, samples, probe.HookError, probe.DroppedTraceCount });
                    foreach (var item in samples)
                    {
                        if (!last.TryGetValue(item.Target.DisplayId, out var previous) || previous != item.Exposure)
                        {
                            Write(log, new { kind = "candidate_change", qpc = item.CompletedAt, display = item.Target.DisplayId, item.Exposure });
                            transitions++;
                        }
                        last[item.Target.DisplayId] = item.Exposure;
                    }
                    foreach (var trace in probe.DrainTrace()) Write(log, new { kind = "trace", trace });
                }
                frames++;
                if (frames % Math.Max(1, 1000 / options.IntervalMs) == 0)
                {
                    process.Refresh();
                    Write(log, new
                    {
                        kind = "resources",
                        qpc = tick,
                        cpuMs = (process.TotalProcessorTime - cpuStart).TotalMilliseconds,
                        process.WorkingSet64,
                        process.PrivateMemorySize64,
                        gdi = Native.GetGuiResources(process.Handle, 0),
                        user = Native.GetGuiResources(process.Handle, 1)
                    });
                    log.Flush();
                }
                if (Stopwatch.GetElapsedTime(started).TotalSeconds >= options.Seconds ||
                    File.Exists(Path.Combine(options.Output, "stop.request"))) break;
            }
        }
        finally
        {
            if (timer != 0 && !Native.KillTimer(0, timer))
                Write(log, new { kind = "timer_cleanup_failed", error = Marshal.GetLastPInvokeError() });
            // Dispose retains ownership if cleanup fails; process exit is recorded as a failure by Main.
            probe?.Dispose();
            if (probe is not null)
                foreach (var trace in probe.DrainTrace()) Write(log, new { kind = "trace", trace });
            fixture?.Dispose();
        }
        if (fixture is not null && !fixture.Completed) throw new InvalidOperationException("Fixture recovery checks did not run.");
        process.Refresh();
        var elapsed = Stopwatch.GetElapsedTime(started);
        var cpuMs = (process.TotalProcessorTime - cpuStart).TotalMilliseconds;
        var summary = new
        {
            kind = "summary",
            options.Mode,
            frames,
            transitions,
            elapsedMs = elapsed.TotalMilliseconds,
            maximumGapMs,
            cpuMs,
            singleCorePercent = cpuMs / elapsed.TotalMilliseconds * 100,
            process.WorkingSet64,
            process.PrivateMemorySize64,
            cleanupConfirmed = true,
            fixturePassed = fixture?.Completed,
            droppedTraceCount = probe?.DroppedTraceCount ?? 0,
            endToEndLatencyMs = (double?)null,
            humanAcceptance = "pending"
        };
        Write(log, summary);
        log.Flush();
        File.WriteAllText(Path.Combine(options.Output, "summary.json"), JsonSerializer.Serialize(summary, Json));
    }

    private static void Write(StreamWriter log, object value) => log.WriteLine(JsonSerializer.Serialize(value, Json));

    private sealed record Options(string Mode, int Seconds, int IntervalMs, string Output);
    private static Options Parse(string[] args)
    {
        var values = new Dictionary<string, string>();
        for (var i = 0; i < args.Length; i += 2)
        {
            if (i + 1 >= args.Length || args[i] is not ("--mode" or "--seconds" or "--interval-ms" or "--output") ||
                !values.TryAdd(args[i], args[i + 1])) throw new ArgumentException("Invalid or duplicate option.");
        }
        var mode = values.GetValueOrDefault("--mode", "observe");
        var seconds = int.Parse(values.GetValueOrDefault("--seconds", "120"));
        var interval = int.Parse(values.GetValueOrDefault("--interval-ms", "16"));
        if (mode is not ("observe" or "baseline" or "fixture") || seconds is < 1 or > 600 || interval is < 10 or > 100)
            throw new ArgumentException("mode=observe|baseline|fixture; seconds=1..600; interval-ms=10..100");
        if (!values.TryGetValue("--output", out var output) || !Path.IsPathFullyQualified(output))
            throw new ArgumentException("--output requires an absolute new directory.");
        if (Directory.Exists(output)) throw new ArgumentException("Output directory already exists; use a new run directory.");
        return new(mode, seconds, interval, output);
    }

    private sealed class Fixture : IDisposable
    {
        private nint first = Parent(), second = Parent();
        private long generation, unaffected;
        public bool Completed { get; private set; }
        public IReadOnlyList<ProbeTarget> Targets() => [new("fixture-a", new(-30000, -30000, 400, 300), 96, (long)first),
            new("fixture-b", new(-30000, -30000, 400, 300), 96, (long)second)];
        public void Advance(int frame)
        {
            if (frame != 3) return;
            if (!ProbeNative.DestroyWindow(first)) throw new InvalidOperationException("Fixture parent did not close.");
            first = Parent();
        }
        public void Verify(int frame, IReadOnlyList<ProbeSnapshot> samples)
        {
            if (samples.Count != 2 || samples.Any(s => !s.Signals.AttributesValid) && frame != 3)
                throw new InvalidOperationException("Fixture child properties failed.");
            if (frame == 0) { generation = samples[0].Generation; unaffected = samples[1].Probe.Handle; }
            if (samples[1].Probe.Handle != unaffected) throw new InvalidOperationException("Other display was disturbed.");
            if (frame == 3 && samples[0].Exposure != ProbeExposure.Unknown) throw new InvalidOperationException("Loss did not suppress.");
            if (frame == 4)
            {
                if (samples[0].Generation <= generation) throw new InvalidOperationException("Recovery was not fresh.");
                Completed = true;
            }
        }
        public void Dispose()
        {
            if (first != 0)
            {
                if (!ProbeNative.DestroyWindow(first)) throw new InvalidOperationException("First fixture parent did not close.");
                first = 0;
            }
            if (second != 0)
            {
                if (!ProbeNative.DestroyWindow(second)) throw new InvalidOperationException("Second fixture parent did not close.");
                second = 0;
            }
        }
        private static nint Parent()
        {
            var window = ProbeNative.CreateWindowExW(0x08000080, "STATIC", "MTP fixture", 0x80000000,
                -30000, -30000, 400, 40, 0, 0, 0, 0);
            if (window == 0) throw new System.ComponentModel.Win32Exception(Marshal.GetLastPInvokeError());
            return window;
        }
    }

    private static class Native
    {
        [StructLayout(LayoutKind.Sequential)]
        internal struct MessageData
        {
            public nint Window;
            public uint Message;
            public nuint WParam;
            public nint LParam;
            public uint Time;
            public int X, Y;
            public uint Private;
        }
        [DllImport("user32.dll", SetLastError = true)] internal static extern nuint SetTimer(nint window, nuint id, uint interval, nint callback);
        [DllImport("user32.dll", SetLastError = true)] internal static extern bool KillTimer(nint window, nuint id);
        [DllImport("user32.dll", SetLastError = true)] internal static extern int GetMessageW(out MessageData msg, nint window, uint first, uint last);
        [DllImport("user32.dll")] internal static extern bool TranslateMessage(ref MessageData msg);
        [DllImport("user32.dll")] internal static extern nint DispatchMessageW(ref MessageData msg);
        [DllImport("user32.dll")] internal static extern uint GetGuiResources(nint process, uint flag);
    }
}
