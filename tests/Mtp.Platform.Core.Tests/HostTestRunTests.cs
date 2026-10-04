using System.Text.Json;
using Mtp.Host;

namespace Mtp.Platform.Core.Tests;

public sealed class HostTestRunTests
{
    [Fact]
    public void StoppedTimeDoesNotCountAsLongSamplingOrProduceCpuIntervalsAcrossRuns()
    {
        var root = Path.Combine(Path.GetTempPath(), "mtp-evidence-" + Guid.NewGuid().ToString("N"));
        try
        {
            var clock = new ManualClock();
            var run = new HostTestRun(root, new(), new { }, new { }, clock);
            run.BeginSampling();
            run.AddSample(new(0, 1, 100, 100, 1, 1, 0));
            clock.Advance(10); run.AddSample(new(10, 2, 100, 100, 1, 1, 0)); run.EndSampling();
            clock.Advance(3600); run.BeginSampling(); run.AddSample(new(3610, 50, 100, 100, 1, 1, 0));
            clock.Advance(10); run.AddSample(new(3620, 51, 100, 100, 1, 1, 0)); run.EndSampling();
            using var report = JsonDocument.Parse(File.ReadAllText(run.Export()));
            Assert.False(report.RootElement.GetProperty("LongMeasurementReached").GetBoolean());
            Assert.Equal(2, report.RootElement.GetProperty("Intervals").GetArrayLength());
            run.BeginSampling();
            clock.Advance(1); run.AddSample(new(3621, 52, 100, 100, 1, 1, 0));
            clock.Advance(1800); run.AddSample(new(5421, 60, 100, 100, 1, 1, 0)); run.EndSampling();
            using var longReport = JsonDocument.Parse(File.ReadAllText(run.Export()));
            Assert.True(longReport.RootElement.GetProperty("LongMeasurementReached").GetBoolean());
        }
        finally { Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData("mica", 0.5, "system", 1)]
    [InlineData("acrylic", 0, "light", 0)]
    [InlineData("none", 0.2, "dark", 60)]
    [InlineData("none", 1, "unknown", 0)]
    [InlineData("none", 1, "system", 120)]
    public void InapplicableConfigurationIsRejected(string material, double alpha, string theme, int hertz)
        => Assert.False(new HostTestConfiguration(material, alpha, theme, hertz).IsValid);

    private sealed class ManualClock : TimeProvider
    {
        private long ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => ticks;
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch.AddTicks(ticks);
        public void Advance(int seconds) => ticks += seconds * TimeSpan.TicksPerSecond;
    }
    [Fact]
    public void EvidenceSeparatesHumanResultsAndMeasuredPhasesAndDoesNotOverwriteEarlierRuns()
    {
        var root = Path.Combine(Path.GetTempPath(), "mtp-evidence-" + Guid.NewGuid().ToString("N"));
        try
        {
            var run = new HostTestRun(root, new HostTestConfiguration(), new { binary = "test-only" }, new { visible = true });
            run.AddSample(new(0, 10, 100, 200, 5, 10, 0));
            run.AddSample(new(10, 12, 120, 220, 6, 11, 30));
            run.MarkVisibility(true);
            run.RecordResult("appearance", "failed", "Human observed wrong appearance");
            run.Finish("cancelled");
            using var json = JsonDocument.Parse(File.ReadAllText(run.Export()));
            var report = json.RootElement;
            Assert.Equal("cancelled", report.GetProperty("Outcome").GetString());
            Assert.Equal(20, report.GetProperty("Intervals")[0].GetProperty("SingleCorePercent").GetDouble());
            Assert.Equal("failed", report.GetProperty("Results").GetProperty("appearance").GetProperty("Status").GetString());
            Assert.Equal("untested", report.GetProperty("Results").GetProperty("gpu").GetProperty("Status").GetString());
            Assert.Equal("human-hidden", report.GetProperty("VisibilityMarkers")[0].GetProperty("Phase").GetString());
            var next = new HostTestRun(root, new(), new { }, new { });
            Assert.NotEqual(run.DirectoryPath, next.DirectoryPath);
            Assert.True(File.Exists(run.Export()));
            Assert.Throws<InvalidOperationException>(() => run.RecordResult("appearance", "passed", "late"));
        }
        finally { Directory.Delete(root, true); }
    }
}
