using Mtp.Broker;
using Mtp.Contracts;
using Mtp.Transport;

try
{
    using var shutdown = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; shutdown.Cancel(); };
    var launch = await LengthPrefixedJson.ReadAsync<BrokerLaunch>(Console.OpenStandardInput(), shutdown.Token);
    await using var broker = new BrokerServer(launch);
    await broker.RunAsync(shutdown.Token);
    return 0;
}
catch (OperationCanceledException) { return 0; }
catch (Exception exception) when (exception is IOException or ArgumentException or InvalidOperationException)
{
    // Never print credentials, business payloads, or exception messages containing either.
    Console.Error.WriteLine("BrokerStopped:" + (exception is ProtocolException protocol ? protocol.Code : exception.GetType().Name));
    return 1;
}
