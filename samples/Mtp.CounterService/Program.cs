using Mtp.Contracts;
using Mtp.Sdk;

try
{
    using var shutdown = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; shutdown.Cancel(); };
    var iterations = GetOption("--iterations", 10000, 0, 100000);
    var interval = GetOption("--interval-ms", 100, 1, 60000);
    await using var client = await SdkClient.ConnectFromStandardInputAsync(applicationId => new CounterProvider(applicationId), shutdown.Token);
    for (var count = 1; count <= iterations; count++)
    {
        await Task.Delay(interval, shutdown.Token);
        var result = await client.PublishAsync(CounterProvider.CreateState(count), shutdown.Token);
        if (!result.Accepted) { Console.Error.WriteLine("CounterRejected:" + result.Code); return 2; }
    }
    return 0;
}
catch (OperationCanceledException) { return 0; }
catch (Exception exception) when (exception is IOException or ArgumentException or InvalidOperationException)
{
    Console.Error.WriteLine("CounterStopped:" + exception.GetType().Name);
    return 1;
}

int GetOption(string name, int fallback, int minimum, int maximum)
{
    var index = Array.IndexOf(args, name);
    if (index < 0) return fallback;
    if (index + 1 >= args.Length || !int.TryParse(args[index + 1], out var value) || value < minimum || value > maximum)
        throw new ArgumentException("InvalidCounterOption");
    return value;
}

sealed class CounterProvider(string applicationId) : IDeclarationProvider
{
    public Task<ApplicationSnapshot> GetSnapshotAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ActionSlotDeclaration[] actions = [new("activate")];
        var declaration = new ApplicationDeclaration(applicationId,
            [new FeatureGroupDeclaration("main", [new ComponentDeclaration("counter", actions)], [new TaskbarFlyoutDeclaration("details", actions)])]);
        return Task.FromResult(new ApplicationSnapshot(declaration, CreateState(0)));
    }

    public static ApplicationState CreateState(long count) => new(count, [new ComponentReading("main", "counter", count.ToString(System.Globalization.CultureInfo.InvariantCulture), count)]);
}
