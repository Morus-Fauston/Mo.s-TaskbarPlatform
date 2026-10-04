using Mtp.Platform.Core;

namespace Mtp.Host;

public enum IslandDisplayState { Hidden, WaitingForTaskbar, Embedded, Failed, CleanupPending, Closed }

/// <summary>The Windows resource boundary. There is deliberately no independent-window fallback.</summary>
public interface IIslandSessionAdapter
{
    bool IsAlive { get; }
    CoreResult<bool> Start();
    CoreResult<bool> Close();
}

/// <summary>UI-thread display policy: one attempt per environment epoch or explicit retry.</summary>
public sealed class IslandDisplaySession(IIslandSessionAdapter adapter)
{
    private readonly List<StructuredError> errors = [];
    private string? observed;
    private bool attempted;
    private bool desired;
    private bool closed;
    public IslandDisplayState State { get; private set; } = IslandDisplayState.Hidden;
    public StructuredError? Error { get; private set; }
    public IReadOnlyList<StructuredError> ErrorHistory => errors.AsReadOnly();
    public bool Desired => desired;

    public void SetIntent(bool visible)
    {
        if (closed || desired == visible) return;
        desired = visible;
        attempted = false;
        if (!visible && Cleanup()) State = IslandDisplayState.Hidden;
    }

    public void Refresh(string? environmentKey)
    {
        if (closed || State == IslandDisplayState.CleanupPending) return;
        if (environmentKey != observed)
        {
            if (!Cleanup()) return;
            State = IslandDisplayState.WaitingForTaskbar;
            observed = environmentKey;
            attempted = false;
        }
        if (!desired) { State = IslandDisplayState.Hidden; return; }
        if (environmentKey is null)
        {
            if (!Cleanup()) return;
            State = IslandDisplayState.WaitingForTaskbar;
            return;
        }
        if (State == IslandDisplayState.Embedded && !adapter.IsAlive)
        {
            Fail(new("island_lost", "内容岛或父级已失效；等待任务栏变化或手动重试。"));
            Cleanup();
            return;
        }
        if (attempted) return;
        attempted = true;
        var result = adapter.Start();
        if (!result.IsSuccess) { Fail(result.Error!); Cleanup(); return; }
        Error = null;
        State = IslandDisplayState.Embedded;
    }

    public void Retry()
    {
        if (closed || !Cleanup()) return;
        State = IslandDisplayState.WaitingForTaskbar;
        attempted = false;
        Refresh(observed);
    }

    public void ReportError(StructuredError error) => Fail(error);

    public bool Shutdown()
    {
        closed = true; // Even failed cleanup must reject late recovery callbacks.
        desired = false;
        if (!Cleanup()) return false;
        State = IslandDisplayState.Closed;
        return true;
    }

    private bool Cleanup()
    {
        var result = adapter.Close();
        if (result.IsSuccess) return true;
        Fail(result.Error!);
        State = IslandDisplayState.CleanupPending;
        return false;
    }

    private void Fail(StructuredError error)
    {
        Error = error;
        State = IslandDisplayState.Failed;
        if (errors.Count == 0 || errors[^1] != error) errors.Add(error);
    }
}
