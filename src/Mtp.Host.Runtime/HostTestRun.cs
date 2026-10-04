using System.Text.Json;
using System.Diagnostics;

namespace Mtp.Host;

public sealed record HostResourceSample(double Seconds, double CpuSeconds, long PrivateBytes, long WorkingSetBytes, int Threads, int Handles, long Updates, int SamplingEpoch = 0);
public sealed record HostEvidenceResult(string Status, string Note, DateTimeOffset? At);
public sealed record HostVisibilityMarker(string Phase, DateTimeOffset At, double Seconds);

/// <summary>One immutable configuration and append-only observations per run, independent of UI.</summary>
public sealed class HostTestRun
{
    private readonly TimeProvider clock;
    private readonly long started;
    private readonly object identity;
    private readonly object initialState;
    private readonly List<HostResourceSample> samples = [];
    private readonly List<HostVisibilityMarker> markers = [];
    private readonly Dictionary<string, HostEvidenceResult> results;
    private readonly List<string> evidenceErrors = [];
    internal void NoteEvidenceFailure(string message) { if (!evidenceErrors.Contains(message)) evidenceErrors.Add(message); }
    private int samplingEpoch;
    private double? samplingStart;
    private readonly List<(double Start, double End)> samplingPeriods = [];
    private static readonly JsonSerializerOptions json = new() { WriteIndented = true };
    public static IReadOnlyList<string> Dimensions { get; } = Array.AsReadOnly(new[]
    { "appearance", "keyboard", "focus", "popup", "blank-hit", "cpu", "memory", "handles", "threads", "long-trend", "real-hidden", "recovery", "gpu", "wakeups", "presentation-fps", "external-uia", "external-display-dpi", "budget" });
    public string DirectoryPath { get; }
    public HostTestConfiguration Configuration { get; }
    public DateTimeOffset StartedAt { get; }
    public DateTimeOffset? EndedAt { get; private set; }
    public string Outcome { get; private set; } = "running";
    public double ElapsedSeconds => clock.GetElapsedTime(started).TotalSeconds;
    public IReadOnlyList<HostResourceSample> Samples => samples.AsReadOnly();
    public IReadOnlyDictionary<string, HostEvidenceResult> Results => new System.Collections.ObjectModel.ReadOnlyDictionary<string, HostEvidenceResult>(results);

    public HostTestRun(string root, HostTestConfiguration configuration, object identity, object initialState, TimeProvider? clock = null)
    {
        if (!configuration.IsValid) throw new ArgumentException("测试配置无效，alpha 仅用于 none；负载为 0/1/30/60 Hz。", nameof(configuration));
        this.clock = clock ?? TimeProvider.System;
        started = this.clock.GetTimestamp();
        StartedAt = this.clock.GetUtcNow();
        Configuration = configuration;
        this.identity = identity;
        this.initialState = initialState;
        DirectoryPath = Path.Combine(root, StartedAt.ToString("yyyyMMdd-HHmmss-fff") + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(DirectoryPath);
        results = Dimensions.ToDictionary(key => key, key => new HostEvidenceResult("untested", key switch
        {
            "gpu" or "wakeups" or "presentation-fps" => "未配置可靠采集器，不能以零或更新频率代替。",
            "external-uia" => "待维护者使用外部 UIA 工具检查 MtpHostIslandContent。",
            "external-display-dpi" => "待相应外屏/混合 DPI 设备与人工验证。",
            "long-trend" => "需要至少 30 分钟实际采样及维护者判断。",
            "budget" => "性能预算由维护者根据证据处置。",
            _ => "待维护者逐项记录；自动观测不构成人工通过。"
        }, null));
        Observe("run-start", new { Configuration, initialState });
        Export();
    }

    public void Observe(string kind, object? value)
    {
        RequireRunning();
        File.AppendAllText(Path.Combine(DirectoryPath, "events.jsonl"), JsonSerializer.Serialize(new { At = clock.GetUtcNow(), Seconds = ElapsedSeconds, Kind = kind, Value = value }) + "\n");
    }
    public void AddSample(HostResourceSample sample)
    {
        RequireRunning();
        if (samples.Count > 0 && sample.Seconds <= samples[^1].Seconds) throw new ArgumentException("采样时点必须递增。");
        sample = sample with { SamplingEpoch = samplingEpoch };
        samples.Add(sample);
        File.AppendAllText(Path.Combine(DirectoryPath, "samples.jsonl"), JsonSerializer.Serialize(sample) + "\n");
    }
    public void BeginSampling()
    {
        RequireRunning();
        if (samplingStart is not null) return;
        samplingEpoch++;
        samplingStart = ElapsedSeconds;
        Observe("sampling-start", new { Configuration.Hertz, Configuration.DurationSeconds, samplingEpoch });
    }
    public void EndSampling()
    {
        if (samplingStart is not double first) return;
        samplingPeriods.Add((first, ElapsedSeconds));
        samplingStart = null;
        Observe("sampling-stop", new { samplingEpoch });
    }
    public void MarkVisibility(bool hidden)
    {
        RequireRunning();
        var marker = new HostVisibilityMarker(hidden ? "human-hidden" : "human-restored", clock.GetUtcNow(), ElapsedSeconds);
        markers.Add(marker);
        Observe("human-visibility-marker", marker);
    }
    public void RecordResult(string dimension, string status, string note)
    {
        RequireRunning();
        if (!results.ContainsKey(dimension) || status is not ("passed" or "failed" or "untested") || string.IsNullOrWhiteSpace(note))
            throw new ArgumentException("请选择证据维度、结果，并填写观察说明或未测原因。");
        var result = new HostEvidenceResult(status, note, clock.GetUtcNow());
        Observe("human-result", new { dimension, result });
        results[dimension] = result;
        Export();
    }
    public void Finish(string outcome)
    {
        if (EndedAt is not null) return;
        try { EndSampling(); Observe("run-stop", outcome); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { NoteEvidenceFailure(error.Message); }
        Outcome = outcome;
        EndedAt = clock.GetUtcNow();
        Export();
    }
    public string Export()
    {
        var intervals = samples.Zip(samples.Skip(1)).Where(pair => pair.First.SamplingEpoch == pair.Second.SamplingEpoch).Select(pair =>
        {
            var first = pair.First;
            var last = pair.Second;
            return new
            {
                StartSeconds = first.Seconds,
                EndSeconds = last.Seconds,
                Phase = markers.LastOrDefault(m => m.Seconds <= first.Seconds)?.Phase ?? "unmarked",
                SingleCorePercent = 100 * (last.CpuSeconds - first.CpuSeconds) / (last.Seconds - first.Seconds),
                MachinePercent = 100 * (last.CpuSeconds - first.CpuSeconds) / (last.Seconds - first.Seconds) / Environment.ProcessorCount,
                First = first,
                Last = last
            };
        }).ToArray();
        var report = new
        {
            Configuration,
            EvidenceErrors = evidenceErrors,
            Configuration.CaseId,
            StartedAt,
            EndedAt,
            Outcome,
            Identity = identity,
            InitialComponentState = initialState,
            ProcessorCount = Environment.ProcessorCount,
            Samples = samples,
            Intervals = intervals,
            VisibilityMarkers = markers,
            Results = results,
            SamplingPeriods = samplingPeriods.Select(p => new { p.Start, p.End }).ToArray(),
            LongMeasurementReached = samples.GroupBy(sample => sample.SamplingEpoch).Any(group => group.Last().Seconds - group.First().Seconds >= 1800),
            Comparison = "本轮仅为当前 Host；旧独立工具数据未导入。无双方二进制、同内容/尺寸/负载/时长，不可直接比较。",
            Limits = "更新频率不是呈现 FPS；阶段由人工标记，不控制任务栏。单次资源差值不证明泄漏或零泄漏。"
        };
        var path = Path.Combine(DirectoryPath, "report.json");
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(report, json));
        File.Move(temporary, path, true);
        static string Cell(string value) => value.Replace("|", "\\|").Replace("\r", "").Replace("\n", "<br>");
        var markdown = new System.Text.StringBuilder();
        markdown.AppendLine($"# Host 案例 {Configuration.CaseId}\n");
        markdown.AppendLine($"- 开始：{StartedAt:O}\n- 结束：{EndedAt?.ToString("O") ?? "运行中"}\n- 状态：{Outcome}\n- 配置：{Configuration.Material} / alpha {Configuration.Alpha} / {Configuration.Theme} / {Configuration.Hertz} Hz / {Configuration.DurationSeconds} 秒预设\n");
        if (evidenceErrors.Count > 0) markdown.AppendLine("证据不完整，写入错误：" + string.Join("；", evidenceErrors) + "\n");
        markdown.AppendLine("原始身份、环境、二进制哈希和资源趋势见 [report.json](report.json)；事件见 [events.jsonl](events.jsonl)，采样见 [samples.jsonl](samples.jsonl)。\n");
        markdown.AppendLine("## 人工逐项记录\n\n| 维度 | 结果 | 说明 | 时点 |\n| --- | --- | --- | --- |");
        foreach (var result in results) markdown.AppendLine($"| {result.Key} | {result.Value.Status} | {Cell(result.Value.Note)} | {result.Value.At?.ToString("O") ?? "未记录"} |");
        markdown.AppendLine("\n## 自动采样\n\n| 阶段/采样轮 | 样本数 | 起止秒 | 私有内存首末（bytes） | 句柄首末 | 线程首末 |\n| --- | --- | --- | --- | --- | --- |");
        foreach (var phase in samples.GroupBy(s => new { s.SamplingEpoch, Phase = markers.LastOrDefault(m => m.Seconds <= s.Seconds)?.Phase ?? "unmarked" }))
        {
            var first = phase.First(); var last = phase.Last();
            markdown.AppendLine($"| {phase.Key.Phase}/{phase.Key.SamplingEpoch} | {phase.Count()} | {first.Seconds:F2} – {last.Seconds:F2} | {first.PrivateBytes} → {last.PrivateBytes} | {first.Handles} → {last.Handles} | {first.Threads} → {last.Threads} |");
        }
        markdown.AppendLine("\n自动观测不构成人工通过。CPU 在 JSON 中同时报告单核等效及整机百分比；无采集器的指标为未测。真实隐藏/恢复仅为人工按钮时点，自动化夹具中的同名按钮记录不是人工证据。更新频率不是呈现 FPS，单次差值不证明泄漏或零泄漏。无同条件的旧工具数据不可直接比较。");
        File.WriteAllText(Path.Combine(DirectoryPath, "report.md"), markdown.ToString());
        return path;
    }
    private void RequireRunning() { if (EndedAt is not null) throw new InvalidOperationException("本轮已结束，迟到记录被拒绝。"); }
}

public sealed class HostProcessSampler : IDisposable
{
    private readonly Process process = Process.GetCurrentProcess();
    public HostResourceSample Read(double seconds, long updates)
    {
        process.Refresh();
        return new(seconds, process.TotalProcessorTime.TotalSeconds, process.PrivateMemorySize64,
            process.WorkingSet64, process.Threads.Count, process.HandleCount, updates);
    }
    public void Dispose() => process.Dispose();
}
