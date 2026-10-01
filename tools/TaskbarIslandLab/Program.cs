using System.Diagnostics;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using TaskbarIslandLab.Logic;
using TaskbarIslandLab.Windows;

namespace TaskbarIslandLab;

internal static class Program
{
    internal static LabOptions Options = null!;
    internal static RunLog Log = null!;
    [STAThread]
    private static int Main(string[] args)
    {
        Environment.ExitCode = 1;
        try
        {
            Options = LabOptions.Parse(args);
            using var log = new RunLog(Options.Output);
            Log = log;
            log.Write("configuration", Options);
            log.Write("environment", new
            {
                os = Environment.OSVersion.VersionString,
                runtime = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
                architecture = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(),
                processors = Environment.ProcessorCount,
                winuiProjection = typeof(Application).Assembly.FullName,
                windowsAppSdkPackage = "2.4.0",
                pid = Environment.ProcessId,
                gpu = "unmeasured: no GPU performance counter or ETW collector enabled",
                wakeupsAndContextSwitches = "unmeasured: no kernel ETW collector enabled",
                renderedFps = "unknown: content update counts are not presentation frame counts"
            });
            if (Options.Scenario == "list-targets")
            {
                log.Save(Path.Combine(Options.Output, "targets.json"), NativeWindows.ListExplorerTargets());
                log.Write("result", new { exitCode = 0, operation = "explicit read-only Explorer target enumeration" });
                return 0;
            }
            WinRT.ComWrappersSupport.InitializeComWrappers();
            Application.Start(startup =>
            {
                var queue = DispatcherQueue.GetForCurrentThread();
                SynchronizationContext.SetSynchronizationContext(new DispatcherQueueSynchronizationContext(queue));
                _ = new LabApplication();
            });
            log.Write("process-exit", new { exitCode = Environment.ExitCode });
            return Environment.ExitCode;
        }
        catch (Exception error)
        {
            // Argument failures do not start WinUI or discover system windows.
            Console.Error.WriteLine(error);
            try { Log?.Write("fatal", error.ToString()); } catch (ObjectDisposedException) { }
            return 2;
        }
    }
}

public sealed partial class LabApplication : Application
{
    private readonly LabOptions options = Program.Options;
    private readonly RunLog log = Program.Log;
    private readonly Stopwatch clock = new();
    private readonly ResourceSampler sampler = new();
    private readonly List<object> phases = [];
    private IslandHost? host;
    private DispatcherQueueTimer? control;
    private System.Threading.Timer? updates;
    private ResourceSnapshot? phaseStart, previousSample;
    private MeasurementPhase? phase;
    private long requested, actual;
    private int queued, phaseGeneration;
    private bool finished;
    private int verifyStep;
    private double lastSample;
    private double phaseDeadline, phaseOffset;

    public LabApplication()
    {
        InitializeComponent();
        UnhandledException += (_, args) =>
        {
            log.Write("unhandled", args.Exception.ToString());
            args.Handled = true;
            Finish(1, "unhandled-exception");
        };
    }
    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        try
        {
            host = new IslandHost(options, log.Write);
            host.StopRequested += reason => { if (options.Scenario != "verify") Finish(reason is "user-close" or "window-closed" ? 0 : 3, reason); };
            host.Start();
            using (var process = Process.GetCurrentProcess())
                log.Write("loaded-runtime-modules", process.Modules.Cast<ProcessModule>()
                    .Where(module => module.ModuleName.Contains("Microsoft.UI.Xaml", StringComparison.OrdinalIgnoreCase) || module.ModuleName.Contains("Microsoft.WindowsAppRuntime", StringComparison.OrdinalIgnoreCase))
                    .Select(module => new { module.ModuleName, module.FileVersionInfo.FileVersion }).ToArray());
            clock.Start();
            control = DispatcherQueue.GetForCurrentThread().CreateTimer();
            control.Interval = TimeSpan.FromMilliseconds(options.Scenario == "verify" ? 350 : 250);
            control.Tick += Tick;
            control.Start();
            if (options.Scenario != "verify") ChangePhase(options.Scenario == "measure" ? MeasurementPhase.At(0, options) : new("manual", true, true));
        }
        catch (Exception error) { log.Write("start-failed", error.ToString()); Finish(1, "start-failed"); }
    }
    private void Tick(DispatcherQueueTimer sender, object args)
    {
        if (finished) return;
        try
        {
            if (File.Exists(options.StopFile)) { Finish(0, "stop-file"); return; }
            if (options.Scenario == "verify")
            {
                if (clock.Elapsed.TotalSeconds > options.TimeoutSeconds) throw new TimeoutException("Owned HWND verification timeout.");
                Verify();
                return;
            }
            var health = host!.CheckHealth();
            if (health != null) { Finish(3, health); return; }
            host.DismissIfParentHidden();
            if (options.Scenario == "measure")
            {
                if (clock.Elapsed.TotalSeconds >= phaseDeadline)
                {
                    phaseOffset += phase!.Duration(options);
                    var next = MeasurementPhase.At(phaseOffset, options);
                    if (next.Name == "finished") { Finish(0, "measurement-complete"); return; }
                    ChangePhase(next);
                }
            }
            else if (clock.Elapsed.TotalSeconds >= options.TimeoutSeconds) { Finish(0, "manual-timeout"); return; }
            if (clock.Elapsed.TotalSeconds - lastSample >= 1)
            {
                lastSample = clock.Elapsed.TotalSeconds;
                var snapshot = sampler.Read(Interlocked.Read(ref requested), actual);
                log.Write("sample", new { phase = phase?.Name, snapshot, interval = previousSample == null ? null : ResourceSampler.Difference(previousSample, snapshot), logicalParentVisible = options.Mode == "explorer" ? NativeWindows.IsWindowVisible(host.ParentHandle) : (bool?)null });
                previousSample = snapshot;
            }
        }
        catch (Exception error) { log.Write("tick-failed", error.ToString()); Finish(1, "tick-failed"); }
    }
    private void ChangePhase(MeasurementPhase next)
    {
        StopUpdates();
        if (next.Name == "idle" && options.Mode != "empty")
        {
            Require(host!.Loaded && host.HasExpectedTree, "measurement baseline has a real loaded control tree");
            Require(host.ContentWidth == 260 && host.ContentHeight == 56, "measurement baseline uses identical DIP content size");
        }
        var snapshot = sampler.Read(Interlocked.Read(ref requested), actual);
        EndPhase(snapshot);
        phase = next;
        phaseDeadline = clock.Elapsed.TotalSeconds + next.Duration(options);
        phaseStart = previousSample = snapshot;
        if (options.Mode != "explorer") host!.SetVisible(next.Visible);
        log.Write("phase-begin", new { phase = next.Name, expectedSeconds = next.Name == "visible" ? options.VisibleSeconds : (int?)null, visibilityControl = options.Mode == "explorer" ? "human-only; phase name is an instruction, not observed visibility" : "owned-window-only" });
        if (next.Update && options.Hz > 0)
        {
            var generation = phaseGeneration;
            var hostToken = host!.Token;
            var queue = DispatcherQueue.GetForCurrentThread();
            updates = new System.Threading.Timer(_ =>
            {
                if (generation != Volatile.Read(ref phaseGeneration)) return;
                Interlocked.Increment(ref requested);
                if (Interlocked.Exchange(ref queued, 1) != 0) return;
                if (!queue.TryEnqueue(() =>
                {
                    Interlocked.Exchange(ref queued, 0);
                    if (finished || generation != phaseGeneration) return;
                    try
                    {
                        if (host.Update(hostToken, Interlocked.Read(ref requested))) actual++;
                        if (options.Diagnostics) log.Write("update-diagnostic", new { requested = Interlocked.Read(ref requested), actual });
                    }
                    catch (Exception error) { log.Write("update-failed", error.ToString()); Finish(1, "update-failed"); }
                })) Interlocked.Exchange(ref queued, 0);
            }, null, TimeSpan.Zero, TimeSpan.FromMilliseconds(1000.0 / options.Hz));
        }
    }
    private void StopUpdates()
    {
        Interlocked.Increment(ref phaseGeneration);
        updates?.Dispose();
        updates = null;
    }
    private void EndPhase(ResourceSnapshot snapshot)
    {
        if (phaseStart == null || phase == null) return;
        var result = new { phase = phase.Name, metrics = ResourceSampler.Difference(phaseStart, snapshot) };
        phases.Add(result);
        log.Write("phase-end", result);
        phaseStart = null;
    }
    private void Verify()
    {
        if (host == null) throw new InvalidOperationException("No host.");
        if (verifyStep < 2 && !host.Loaded) return;
        switch (verifyStep++)
        {
            case 0:
                Require(host.Loaded && host.HasExpectedTree, "real WinUI content tree loaded");
                Require(host.ContentWidth == 260 && host.ContentHeight == 56, "260 x 56 DIP root layout");
                AssertNativeLayout(260, 56);
                Require(host.CheckHealth() == null, "initial parent/bridge/DPI identity");
                var before = NativeWindows.GetForegroundWindow();
                host.SetVisible(false);
                Require(host.CheckHealth() == null, "hidden parent is not binding failure");
                host.SetVisible(true);
                Require(NativeWindows.GetForegroundWindow() == before, "show/hide did not change foreground HWND in fixture");
                host.ResizeOwnedFixture(300, 72);
                break;
            case 1:
                AssertNativeLayout(300, 72);
                host.ResizeOwnedFixture(260, 56);
                var oldToken = host.Token;
                host.Close("cycle");
                host.Close("repeated-close");
                Require(!host.Update(oldToken, 99), "closed instance rejects updates");
                host.Start();
                Require(!host.Update(oldToken, 99), "recreated instance rejects stale callback");
                break;
            case 2:
                Require(host.Loaded, "recreated XAML tree loaded");
                host.LoseOwnedParentRelationFixture();
                Require(host.CheckHealth() == "parent-relation-lost", "detached parent relation is detected");
                host.Close("parent-relation-lost");
                host.Start();
                break;
            case 3:
                host.DestroyOwnedParentFixture();
                Require(host.CheckHealth() != null, "destroyed parent invalidates instance");
                host.Close("parent-destroyed");
                Require(host.State == LifetimeState.Closed, "parent destruction cleaned DWXS safely");
                host.Start();
                break;
            case 4:
                Require(host.Loaded, "explicit rebuild after parent destruction");
                host.OpenPopupAtEdgeFixture(true);
                break;
            case 5:
                Require(host.PopupFitsDisplayFixture(), "bottom-right popup panel, text and close button fit display");
                host.InvokePopupCloseFixture();
                break;
            case 6:
                Require(!host.PopupIsOpen, "popup close button invocation closes popup");
                Require(host.PopupWindowsHiddenFixture(), "close button hides native popup window");
                host.OpenPopupAtEdgeFixture(false);
                break;
            case 7:
                Require(host.PopupFitsDisplayFixture(), "top-left popup panel, text and close button fit display");
                host.SetVisible(false);
                Require(!host.PopupIsOpen, "hiding owned parent dismisses popup");
                host.SetVisible(true);
                host.OpenPopupAtEdgeFixture(true);
                break;
            case 8:
                Require(host.PopupIsOpen, "popup reopens before instance cleanup");
                Require(host.PopupFitsDisplayFixture(), "reopened popup remains inside display");
                host.Close("before-failure-fixtures");
                Require(!host.PopupIsOpen && host.State == LifetimeState.Closed, "instance cleanup closes open popup");
                Require(host.PopupWindowsDestroyedFixture(), "instance cleanup destroys native popup window");
                foreach (var failAfter in new[] { "host", "source", "attach" })
                {
                    var failed = false;
                    try { host.Start(failAfter); }
                    catch (InvalidOperationException error) when (error.Message.StartsWith("Injected", StringComparison.Ordinal)) { failed = true; }
                    Require(failed && host.State == LifetimeState.Closed, "partial initialization cleanup: " + failAfter);
                    host.Close("repeat-failed-close");
                }
                Finish(0, "owned-integration-passed");
                break;
        }
    }
    private void AssertNativeLayout(int widthDip, int heightDip)
    {
        var width = (int)Math.Round(widthDip * host!.Dpi / 96.0);
        var height = (int)Math.Round(heightDip * host.Dpi / 96.0);
        foreach (var hwnd in new[] { host.HostHandle, host.BridgeHandle })
        {
            var rect = NativeWindows.Client(hwnd);
            Require(rect.Right == width && rect.Bottom == height, $"native client layout {width} x {height}: 0x{hwnd:X}");
        }
    }
    private void Require(bool condition, string label)
    {
        log.Write("assertion", new { label, passed = condition });
        if (!condition) throw new InvalidOperationException(label);
    }
    private void Finish(int exitCode, string reason)
    {
        if (finished) return;
        finished = true;
        control?.Stop();
        if (control != null) control.Tick -= Tick;
        StopUpdates();
        EndPhase(sampler.Read(Interlocked.Read(ref requested), actual));
        try { host?.Close(reason); }
        catch (Exception error) { log.Write("cleanup-failed", error.ToString()); exitCode = 4; }
        log.Save(Path.Combine(options.Output, "summary.json"), new
        {
            exitCode,
            reason,
            configuration = options,
            elapsedSeconds = clock.Elapsed.TotalSeconds,
            requestedUpdates = Interlocked.Read(ref requested),
            actualContentUpdates = actual,
            phases,
            sampler.SamplingWallMilliseconds,
            log.WriteWallMilliseconds,
            gpu = "unmeasured: GPU counters/ETW not enabled",
            wakeups = "unmeasured: kernel ETW not enabled",
            presentationFps = "unknown",
            humanAcceptance = "pending",
            cpuPerUpdateMeaning = "average process CPU cost, not precise single render duration",
            diagnosticCostMeaning = "sampler and JSON write wall cost; included in process CPU; compare diagnostics on/off independently"
        });
        log.Write("result", new { exitCode, reason });
        sampler.Dispose();
        Environment.ExitCode = exitCode;
        Exit();
    }
}
