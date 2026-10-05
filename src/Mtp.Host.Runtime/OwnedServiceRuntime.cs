using System.Diagnostics;

namespace Mtp.Host;

internal sealed class OwnedServiceRuntime(Process process, string path, IReadOnlyList<string> arguments)
{
    public Process Process { get; } = process;
    public int ProcessId { get; } = process.Id;
    public bool Stopped;
    public long StartedAt { get; } = Stopwatch.GetTimestamp();
    public bool InitialConnectionConfirmed;
    public string Path { get; } = path;
    public IReadOnlyList<string> Arguments { get; } = Array.AsReadOnly(arguments.ToArray());
    public SemaphoreSlim InputWriter { get; } = new(1, 1);
    public bool InputFailed;
    public long BrokerGeneration;
    public string? SessionId;
    public Task OutputDrain { get; } = DrainOutput(process.StandardOutput);
    public Task ErrorDrain { get; } = DrainOutput(process.StandardError);

    private static Task DrainOutput(StreamReader reader) => Task.Factory.StartNew(() =>
    {
        // Inherited Windows anonymous pipes have synchronous handles. Dedicated readers
        // keep their blocking lifetime off the shared thread pool, with no output cache.
        var buffer = new char[1024];
        try { while (reader.Read(buffer, 0, buffer.Length) != 0) { } }
        catch (Exception error) when (error is IOException or ObjectDisposedException) { }
    }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
}
