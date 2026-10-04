using Microsoft.UI.Xaml;
using Mtp.Host.Islands;
using Mtp.Platform.Core;

namespace Mtp.Host;

/// <summary>Owns temporary scenarios, sampling timers and evidence. Never writes preferences.</summary>
internal sealed class HostTestController
{
    private readonly IslandDisplayAdapter adapter;
    private readonly Action<bool> refresh;
    private readonly Func<object> currentState;
    private readonly string evidenceRoot;
    private readonly Func<bool> canStart;
    private readonly DispatcherTimer sampleTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly DispatcherTimer updateTimer = new();
    private HostProcessSampler? sampler;
    private long updates;
    private double sampleStart;
    private bool closing;
    private readonly List<HostTestRun> history = [];
    public IReadOnlyList<HostTestRun> History => history.AsReadOnly();
    public HostTestRun? Run { get; private set; }
    public HostTestRun? LastRun { get; private set; }
    public HostTestConfiguration Configuration => Run?.Configuration ?? new();
    public bool IsRunning => Run is not null;
    public bool IsSampling => sampler is not null;
    public string Status { get; private set; } = "未运行案例";
    public string? LastError { get; private set; }
    public event Action? Changed;
    public HostTestController(IslandDisplayAdapter adapter, Action<bool> refresh, Func<object> currentState, string evidenceRoot, Func<bool>? canStart = null)
    {
        this.adapter = adapter; this.refresh = refresh; this.currentState = currentState; this.evidenceRoot = evidenceRoot;
        this.canStart = canStart ?? (() => true);
        sampleTimer.Tick += (_, _) => Guard(SampleTick);
        updateTimer.Tick += (_, _) => Guard(() => { if (Run is not null && adapter.IsAlive) adapter.Update(++updates); });
    }
    public void Start(HostTestConfiguration configuration)
    {
        if (closing) return;
        if (!canStart()) throw new InvalidOperationException("先关闭独立预览，再开始任务栏案例，避免混入预览资源开销。");
        if (Run is not null) { Status = "已有案例运行；先停止当前案例。"; Changed?.Invoke(); return; }
        Run = new HostTestRun(evidenceRoot, configuration, HostEvidenceIdentity.Capture(adapter.TargetDescription), currentState());
        LastRun = Run;
        history.Add(Run);
        updates = 0;
        Status = "运行中：" + configuration.CaseId;
        refresh(false);
        adapter.Observe();
        Changed?.Invoke();
    }
    public void StartSampling()
    {
        if (Run is null) throw new InvalidOperationException("先开始一个当前案例，再启动该案例的采样。");
        if (sampler is not null) return;
        if (!adapter.IsAlive) throw new InvalidOperationException("内容岛尚未成功嵌入，不能启动目标采样。");
        sampler = new HostProcessSampler();
        sampleStart = Run.ElapsedSeconds;
        Run.BeginSampling();
        Sample();
        if (Configuration.Hertz > 0)
        {
            updateTimer.Interval = TimeSpan.FromSeconds(1d / Configuration.Hertz);
            updateTimer.Start();
        }
        sampleTimer.Start();
        Status = "采样中：" + Run.Configuration.CaseId;
        Changed?.Invoke();
    }
    private void SampleTick()
    {
        Sample();
        if (Run is not null && Run.ElapsedSeconds - sampleStart >= Configuration.DurationSeconds) StopSampling();
        Changed?.Invoke();
    }
    private void Sample()
    {
        if (Run is not null && sampler is not null) Run.AddSample(sampler.Read(Run.ElapsedSeconds, updates));
    }
    public void StopSampling()
    {
        sampleTimer.Stop(); updateTimer.Stop();
        if (sampler is null) return;
        try { Sample(); Run?.EndSampling(); }
        catch (Exception error) { EvidenceFailure(Run, error); }
        finally { sampler.Dispose(); sampler = null; }
        Status = "采样已停止，可记录结果或停止案例。";
        try { Run?.Export(); }
        catch (Exception error) { EvidenceFailure(Run, error); }
        Changed?.Invoke();
    }
    public void MarkVisibility(bool hidden)
    {
        if (!IsSampling) throw new InvalidOperationException("仅在采样期间记录维护者观察到的真实隐藏/恢复时点。");
        Sample(); Run!.MarkVisibility(hidden); Changed?.Invoke();
    }
    public void SetPopup(bool open)
    {
        if (Run is null || !Configuration.Controls || !adapter.IsAlive) throw new InvalidOperationException("先运行测试控件案例，并成功嵌入内容岛。");
        adapter.SetPopup(open); Changed?.Invoke();
    }
    public void RecordResult(string dimension, string status, string note)
    {
        if (Run is null) throw new InvalidOperationException("请在停止案例之前记录当前案例结果。");
        Run.RecordResult(dimension, status, note); Status = $"已记录 {dimension}：{status}"; Changed?.Invoke();
    }
    public string Export() => (Run ?? LastRun)?.Export() ?? throw new InvalidOperationException("尚无本轮证据，请先开始案例。");
    public string EvidenceDirectory => (Run ?? LastRun)?.DirectoryPath ?? evidenceRoot;
    public void Observe(string kind, object? value)
    {
        try { Run?.Observe(kind, value); }
        catch (Exception error) { EvidenceFailure(Run, error); }
    }
    public void Stop(string reason = "stopped")
    {
        if (Run is null) return;
        StopSampling();
        var previous = Run;
        adapter.SetPopup(false);
        Observe("test-resources-release", null);
        var cleanup = adapter.Close();
        if (!cleanup.IsSuccess) throw new InvalidOperationException(cleanup.Error!.Message);
        Run = null;
        refresh(true); // Restores latest controller intent; never writes the pre-test snapshot.
        try { previous.Observe("restored-current-intent", currentState()); }
        catch (Exception error) { EvidenceFailure(previous, error); }
        try { previous.Finish(reason); }
        catch (Exception error) { EvidenceFailure(previous, error); }
        Status = "案例已结束：" + reason;
        Changed?.Invoke();
    }
    public void Shutdown()
    {
        closing = true;
        try { Stop("host-closed"); }
        finally { sampleTimer.Stop(); updateTimer.Stop(); sampler?.Dispose(); sampler = null; }
    }
    private void EvidenceFailure(HostTestRun? run, Exception error)
    {
        LastError = "证据写入失败，保留未完整证据并继续清理资源：" + error.Message;
        run?.NoteEvidenceFailure(error.Message);
    }
    private void Guard(Action action)
    {
        try { action(); }
        catch (Exception error)
        {
            LastError = error.Message;
            sampleTimer.Stop(); updateTimer.Stop(); sampler?.Dispose(); sampler = null;
            Status = "测试执行失败，请停止案例并保留证据。";
            Changed?.Invoke();
        }
    }
}
