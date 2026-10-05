using System.IO.Pipes;
using System.Threading.Channels;
using Mtp.Contracts;

namespace Mtp.Host;

internal sealed class BrokerGeneration : IDisposable
{
    public BrokerGeneration(long id, IEnumerable<string> applications, CancellationToken lifetime)
    {
        Id = id;
        string run = Guid.NewGuid().ToString("N");
        HostPipeName = "mtp-host-" + run;
        ServicePipeName = "mtp-services-" + run;
        Control = new NamedPipeServerStream(HostPipeName, PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        Lifetime = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
        Registrations = applications.ToDictionary(id => id, HostBrokerSession.NewRegistration, StringComparer.Ordinal);
    }

    public long Id { get; }
    public string HostPipeName { get; }
    public string ServicePipeName { get; }
    public NamedPipeServerStream Control { get; }
    public CancellationTokenSource Lifetime { get; }
    public SemaphoreSlim Writer { get; } = new(1, 1);
    public Dictionary<string, LaunchRegistration> Registrations { get; }
    public Dictionary<string, (string Application, TaskCompletionSource<ProtocolResult> Completion)> PendingRegistrations { get; } = new(StringComparer.Ordinal);
    public Channel<ProtocolMessage> Actions { get; } = Channel.CreateBounded<ProtocolMessage>(
        new BoundedChannelOptions(ProtocolLimits.MaximumPendingRequests) { SingleReader = true });
    public OwnedServiceRuntime? Broker;
    public Task Receiver = Task.CompletedTask;
    public Task Dispatcher = Task.CompletedTask;
    public Task PermissionPublisher = Task.CompletedTask;
    public DisplayPermissionOutbox Permissions { get; } = new();
    public Dictionary<string, (string Session, long Revision)> PermissionRevisions { get; } = new(StringComparer.Ordinal);
    public bool Ready;
    public bool Faulted;
    public int PeakPending;

    public void Stop()
    {
        Ready = false;
        Lifetime.Cancel();
        Control.Dispose();
        Actions.Writer.TryComplete();
        Permissions.Complete();
        PermissionRevisions.Clear();
        foreach (var pending in PendingRegistrations.Values)
            pending.Completion.TrySetResult(ProtocolResult.Reject("BrokerUnavailable", "Broker连接不可用"));
        PendingRegistrations.Clear();
    }

    public void Dispose()
    {
        Control.Dispose();
        Lifetime.Dispose();
        Writer.Dispose();
    }
}
