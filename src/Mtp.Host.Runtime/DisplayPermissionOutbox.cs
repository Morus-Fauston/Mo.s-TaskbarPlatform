using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Mtp.Contracts;

namespace Mtp.Host;

/// <summary>One latest pending snapshot per registered application, consumed by one writer.</summary>
internal sealed class DisplayPermissionOutbox
{
    private readonly object gate = new();
    private readonly Dictionary<string, ProtocolMessage> pending = new(StringComparer.Ordinal);
    private readonly Channel<bool> changed = Channel.CreateBounded<bool>(new BoundedChannelOptions(1)
    { SingleReader = true, FullMode = BoundedChannelFullMode.DropWrite });
    private bool stopped;

    public bool TryPublish(ProtocolMessage message)
    {
        lock (gate)
        {
            if (stopped || !pending.ContainsKey(message.ApplicationId) && pending.Count >= ProtocolLimits.MaximumApplications) return false;
            pending[message.ApplicationId] = message;
            changed.Writer.TryWrite(true);
            return true;
        }
    }

    public async IAsyncEnumerable<ProtocolMessage> ReadAllAsync([EnumeratorCancellation] CancellationToken token)
    {
        await foreach (var _ in changed.Reader.ReadAllAsync(token).ConfigureAwait(false))
        {
            while (true)
            {
                ProtocolMessage message;
                lock (gate)
                {
                    if (stopped || pending.Count == 0) break;
                    var first = pending.First();
                    message = first.Value; pending.Remove(first.Key);
                }
                yield return message;
            }
        }
    }

    public void Complete()
    {
        lock (gate) { stopped = true; pending.Clear(); changed.Writer.TryComplete(); }
    }
}
