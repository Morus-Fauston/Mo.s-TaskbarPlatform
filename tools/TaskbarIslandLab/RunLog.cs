using System.Diagnostics;
using System.Text.Json;

namespace TaskbarIslandLab;

internal sealed class RunLog : IDisposable
{
    private readonly StreamWriter writer;
    private readonly Stopwatch clock = Stopwatch.StartNew();
    private readonly object gate = new();
    private static readonly JsonSerializerOptions Json = new() { IncludeFields = true };
    public double WriteWallMilliseconds { get; private set; }
    public RunLog(string directory)
    {
        Directory.CreateDirectory(directory);
        writer = new StreamWriter(new FileStream(Path.Combine(directory, "events.jsonl"), FileMode.CreateNew, FileAccess.Write, FileShare.Read));
    }
    public void Write(string name, object? value)
    {
        var begin = Stopwatch.GetTimestamp();
        lock (gate)
        {
            writer.WriteLine(JsonSerializer.Serialize(new { utc = DateTime.UtcNow, elapsedSeconds = clock.Elapsed.TotalSeconds, name, value }, Json));
            writer.Flush();
            WriteWallMilliseconds += Stopwatch.GetElapsedTime(begin).TotalMilliseconds;
        }
    }
    public void Save(string path, object value) => File.WriteAllText(path, JsonSerializer.Serialize(value, new JsonSerializerOptions(Json) { WriteIndented = true }));
    public void Dispose() { lock (gate) writer.Dispose(); }
}
