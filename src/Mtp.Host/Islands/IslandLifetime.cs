namespace Mtp.Host.Islands;

public enum LifetimeState { Closed, Starting, Running, CleanupPending }

/// <summary>UI-thread lifetime. Invalidates callbacks before releasing owned resources.</summary>
public sealed class IslandLifetime
{
    private readonly List<(string Name, Action Release)> resources = [];
    private long generation;
    public LifetimeState State { get; private set; }
    public string? StopReason { get; private set; }
    public IReadOnlyList<string> CleanupErrors { get; private set; } = [];

    public long Begin()
    {
        if (State != LifetimeState.Closed) throw new InvalidOperationException("Previous instance still owns resources.");
        State = LifetimeState.Starting;
        StopReason = null;
        return ++generation;
    }

    public void Own(string name, Action release)
    {
        if (State != LifetimeState.Starting) throw new InvalidOperationException("Resources must be registered during initialization.");
        resources.Add((name, release));
    }

    public void Ready(long token)
    {
        if (token != generation || State != LifetimeState.Starting) throw new InvalidOperationException("Stale initialization.");
        State = LifetimeState.Running;
    }

    public bool Accepts(long token) => State == LifetimeState.Running && generation == token;

    public void Stop(string reason)
    {
        if (State == LifetimeState.Closed) return;
        ++generation;
        StopReason ??= reason;
        State = LifetimeState.CleanupPending;
        var errors = new List<string>();
        // Stop at the failed boundary: do not destroy a parent beneath an unreleased source.
        while (resources.Count > 0)
        {
            var item = resources[^1];
            try { item.Release(); resources.RemoveAt(resources.Count - 1); }
            catch (Exception error) { errors.Add($"{item.Name}: {error.Message}"); break; }
        }
        CleanupErrors = errors;
        if (resources.Count == 0) State = LifetimeState.Closed;
    }
}

