using Mtp.Contracts;

namespace Mtp.Host;

/// <summary>UI-thread ownership of one pending replacement per screen and its terminal receipt.</summary>
public sealed class HostFlyoutReplacementTracker(FlyoutRequestRouter router)
{
    private readonly Dictionary<string, ProtocolMessage> pending = new(StringComparer.Ordinal);
    public IReadOnlyList<KeyValuePair<string, ProtocolMessage>> Snapshot() => pending.ToArray();

    public void Track(string screenId, ProtocolMessage message)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(screenId);
        ArgumentNullException.ThrowIfNull(message);
        if (screenId.Length > 256 || message.Kind != MessageKind.FlyoutRequest || message.Flyout is null)
            throw new ArgumentException("Invalid pending flyout replacement.");
        if (!pending.ContainsKey(screenId) && pending.Count >= ItemPresentationLimits.MaximumScreens)
            throw new InvalidOperationException("Pending flyout screen budget exceeded.");
        if (pending.TryGetValue(screenId, out var previous))
        {
            if (ReferenceEquals(previous, message)) return;
            Complete(screenId, previous, ProtocolResult.Reject("FlyoutSuperseded", "待替换浮窗已被该屏幕的新请求取代"));
        }
        pending[screenId] = message;
    }

    public bool Complete(string screenId, ProtocolMessage expected, ProtocolResult result)
    {
        if (!pending.TryGetValue(screenId, out var current) || !ReferenceEquals(current, expected)) return false;
        pending.Remove(screenId);
        return router.RecordPresentationResult(current.ApplicationId, current.SessionId, current.Flyout!, result, "Replacing");
    }

    public void Clear()
    {
        foreach (var pair in Snapshot())
            Complete(pair.Key, pair.Value, ProtocolResult.Reject("HostClosed", "Host已结束待替换浮窗请求"));
    }
}
