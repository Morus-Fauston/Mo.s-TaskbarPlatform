using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Mtp.Host.Flyouts;
using Mtp.Platform.Core;

namespace Mtp.Host.WindowTests;

internal static class HostOwnedFlyoutStackNativeTests
{
    internal static async Task RunAsync(Action<string> log)
    {
        nint previousForeground = FlyoutNative.GetForegroundWindow();
        var windows = new List<Window>();
        var handles = new List<nint>();
        Process? child = null;
        string directory = Path.Combine(AppContext.BaseDirectory, "flyout-stack-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var work = DisplayArea.Primary.WorkArea;
        var bounds = new PixelRect(work.X + 80, work.Y + 80, 300, 160);
        bool forcedExit = false;
        try
        {
            var focus = Create("MTP stack owned focus", false);
            focus.Window.Activate();
            ((Button)focus.Window.Content).Focus(FocusState.Keyboard);
            await Until(() => FlyoutNative.GetForegroundWindow() == focus.Handle, "Owned stack focus target did not activate.");
            var front = Create("MTP stack hint foreground");
            var back = Create("MTP stack hint background");
            var events = Create("MTP stack event");
            var taskbar = Create("MTP stack taskbar");
            nint[] lowToHigh = [taskbar.Handle, events.Handle, back.Handle, front.Handle];
            await Task.Delay(50);
            FlyoutNative.Position(taskbar.Handle, bounds);
            var before = Capture(lowToHigh);
            Check(!Order(lowToHigh).SequenceEqual(lowToHigh.Reverse()), "Stack fixture did not produce an incorrect initial order.");

            var repaired = Apply();
            Check(repaired.IsSuccess && repaired.Value > 0, "Incorrect native stack was not repaired: " + repaired.Error?.Code);
            CheckOrder(); CheckPreserved(before);
            var stable = Apply();
            Check(stable.IsSuccess && stable.Value == 0, "Stable native stack performed redundant writes.");

            // A lower-priority window's next animation frame promotes it back to TOPMOST.
            FlyoutNative.Position(taskbar.Handle, bounds);
            Check(Order(lowToHigh)[0] == taskbar.Handle, "Lower layer TOPMOST precondition failed.");
            var raised = Apply();
            Check(raised.IsSuccess && raised.Value > 0, "Lower-layer TOPMOST was not repaired.");
            CheckOrder(); CheckPreserved(before);
            var duplicate = HostOwnedFlyoutStack.Apply([front.Handle, taskbar.Handle], [events.Handle, events.Handle], [back.Handle, front.Handle]);
            Check(duplicate.IsSuccess && duplicate.Value == 0, "Deduplication did not retain the highest occurrence's order.");
            log("flyout-stack-order: " + JsonSerializer.Serialize(new
            {
                initialWrites = repaired.Value, restoredWrites = raised.Value, stableWrites = stable.Value,
                duplicateWrites = duplicate.Value, highestFirst = Order(lowToHigh).Select(x => x.ToInt64()).ToArray(),
                foreground = FlyoutNative.GetForegroundWindow().ToInt64(), preserved = before
            }));

            // Every invalid batch starts with a valid but deliberately incorrect stack;
            // any premature native write would change its captured relative order.
            Reject(() => HostOwnedFlyoutStack.Apply([taskbar.Handle], [events.Handle], [back.Handle, front.Handle, 0]),
                "FlyoutStackInvalidOwner", "zero HWND");
            var stale = Create("MTP stack stale owner");
            stale.Window.Close();
            Check(!FlyoutNative.IsWindow(stale.Handle), "Stale HWND precondition failed.");
            Reject(() => HostOwnedFlyoutStack.Apply([taskbar.Handle], [events.Handle], [back.Handle, front.Handle, stale.Handle]),
                "FlyoutStackInvalidOwner", "closed HWND");
            Reject(() => HostOwnedFlyoutStack.Apply(null!, [events.Handle], [front.Handle]),
                "FlyoutStackInvalidInput", "null source");
            int enumerated = 0;
            Reject(() => HostOwnedFlyoutStack.Apply(Unbounded(), [], []), "FlyoutStackInputBudgetExceeded", "unbounded input");
            Check(enumerated == HostOwnedFlyoutStack.MaximumInputHandles + 1, "Input enumeration did not stop at its budget.");
            Reject(() => HostOwnedFlyoutStack.Apply(Throwing(), [], []), "FlyoutStackInvalidInput", "throwing source");

            var start = new ProcessStartInfo(Environment.ProcessPath ?? throw new InvalidOperationException("Test executable is unavailable."))
            {
                UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
                WorkingDirectory = AppContext.BaseDirectory
            };
            start.ArgumentList.Add("--hint-input-target"); start.ArgumentList.Add(directory);
            child = Process.Start(start) ?? throw new InvalidOperationException("Owned external HWND fixture failed to start.");
            string readyPath = Path.Combine(directory, "target.json");
            await Until(() =>
            {
                Check(!child.HasExited, "External HWND fixture exited before publishing its window.");
                return File.Exists(readyPath);
            }, "External HWND fixture did not become ready.");
            using var ready = JsonDocument.Parse(File.ReadAllText(readyPath));
            nint external = new(ready.RootElement.GetProperty("Handle").GetInt64());
            Check(ready.RootElement.GetProperty("ProcessId").GetInt32() == child.Id && FlyoutNative.IsWindow(external),
                "External fixture ownership is invalid.");
            FlyoutNative.GetWindowThreadProcessId(external, out uint actualProcess);
            Check(actualProcess == child.Id && actualProcess != Environment.ProcessId, "External fixture PID was not distinct.");
            Reject(() => HostOwnedFlyoutStack.Apply([taskbar.Handle], [events.Handle], [back.Handle, front.Handle, external]),
                "FlyoutStackInvalidOwner", "external-process HWND", external);
            File.WriteAllText(Path.Combine(directory, "exit.request"), "stop");
            await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            Check(child.ExitCode == 0 && !FlyoutNative.IsWindow(external), "External fixture did not release its HWND.");
            using var exit = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "exit.json")));
            Check(exit.RootElement.GetProperty("WindowClosed").GetBoolean(), "External fixture did not confirm native cleanup.");
            foreach (string count in new[] { "Presses", "Releases", "Clicks" })
                Check(exit.RootElement.GetProperty("Counts").GetProperty(count).GetInt32() == 0,
                    "Stack fixture unexpectedly sent pointer input to the external process.");

            Check(Apply().IsSuccess, "Final stack repair failed."); CheckOrder(); CheckPreserved(before);
            for (int index = 0; index < windows.Count; index++)
                if (FlyoutNative.IsWindow(handles[index])) windows[index].Close();
            Check(handles.All(x => !FlyoutNative.IsWindow(x)), "Stack test retained an owned HWND.");
            log($"PASS: owned native stack hint foreground > hint background > events > taskbar; lower TOPMOST repaired; stable writes=0; geometry/foreground/windows preserved; six rejected input cases; external PID={child.Id}; resources=0.");

            CoreResult<int> Apply() => HostOwnedFlyoutStack.Apply([taskbar.Handle], [events.Handle], [back.Handle, front.Handle]);
            void CheckOrder() => Check(Order(lowToHigh).SequenceEqual(lowToHigh.Reverse()), "Native flyout priorities or hint sibling order are wrong.");
            void CheckPreserved(WindowState[] state)
            {
                Check(Capture(lowToHigh).SequenceEqual(state), "Stack repair changed a window's geometry, styles, or lifetime.");
                Check(FlyoutNative.GetForegroundWindow() == focus.Handle, "Stack repair activated another window.");
                Check(lowToHigh.All(FlyoutNative.IsWindow), "A higher layer closed a lower-priority window.");
            }
            void Reject(Func<CoreResult<int>> action, string expectedCode, string reason, nint external = 0)
            {
                FlyoutNative.Position(taskbar.Handle, bounds);
                nint[] included = external == 0 ? lowToHigh : [.. lowToHigh, external];
                var nativeOrder = Order(included); var nativeState = Capture(included);
                nint active = FlyoutNative.GetForegroundWindow();
                var result = action();
                Check(!result.IsSuccess && result.Error?.Code == expectedCode, "Input was not rejected: " + reason);
                Check(Order(included).SequenceEqual(nativeOrder) && Capture(included).SequenceEqual(nativeState) &&
                    FlyoutNative.GetForegroundWindow() == active, "Rejected input caused native mutation: " + reason);
                log("flyout-stack-rejected: " + JsonSerializer.Serialize(new { reason, Code = result.Error?.Code, nativeOrderUnchanged = true }));
            }
            IEnumerable<nint> Unbounded() { while (true) { enumerated++; yield return taskbar.Handle; } }
            IEnumerable<nint> Throwing() { yield return taskbar.Handle; throw new InvalidOperationException("injected enumeration failure"); }
        }
        finally
        {
            try
            {
                if (child is not null && !child.HasExited)
                {
                    File.WriteAllText(Path.Combine(directory, "exit.request"), "stop");
                    try { await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)); }
                    catch (TimeoutException) { forcedExit = true; child.Kill(); await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3)); }
                }
            }
            finally
            {
                for (int index = 0; index < windows.Count; index++)
                    if (FlyoutNative.IsWindow(handles[index])) windows[index].Close();
                log("flyout-stack-cleanup: " + JsonSerializer.Serialize(new
                { ownedWindowsRemaining = handles.Count(FlyoutNative.IsWindow), childExited = child?.HasExited, forcedExit, evidence = directory }));
                child?.Dispose();
                if (previousForeground != 0 && FlyoutNative.IsWindow(previousForeground)) SetForegroundWindow(previousForeground);
            }
        }

        (Window Window, nint Handle) Create(string title, bool overlay = true)
        {
            var window = new Window { Title = title, Content = new Button { Content = title } };
            windows.Add(window);
            nint handle = WinRT.Interop.WindowNative.GetWindowHandle(window); handles.Add(handle);
            if (window.AppWindow.Presenter is OverlappedPresenter presenter) presenter.SetBorderAndTitleBar(false, false);
            if (overlay) FlyoutNative.NoActivate(handle, true);
            window.AppWindow.MoveAndResize(new(bounds.X, bounds.Y, bounds.Width, bounds.Height));
            window.AppWindow.Show(false);
            if (overlay) FlyoutNative.Position(handle, bounds);
            return (window, handle);
        }
    }

    private static WindowState[] Capture(IEnumerable<nint> handles) => handles.Select(handle => new WindowState(
        handle.ToInt64(), FlyoutNative.Bounds(handle), FlyoutNative.GetWindowLongPtrW(handle, -20).ToInt64())).ToArray();
    private static nint[] Order(IEnumerable<nint> handles)
    {
        var expected = handles.ToHashSet(); var result = new List<nint>();
        nint current = GetTopWindow(0);
        for (int i = 0; current != 0 && i < HostOwnedFlyoutStack.MaximumObservedWindows; i++, current = FlyoutNative.GetWindow(current, 2))
        {
            if (expected.Contains(current)) result.Add(current);
            if (result.Count == expected.Count) return result.ToArray();
        }
        throw new InvalidOperationException("Native stack readback could not find all fixture windows.");
    }
    private static async Task Until(Func<bool> condition, string failure)
    {
        var clock = Stopwatch.StartNew();
        while (!condition()) { if (clock.Elapsed > TimeSpan.FromSeconds(8)) throw new TimeoutException(failure); await Task.Delay(25); }
    }
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private sealed record WindowState(long Handle, PixelRect Bounds, long ExtendedStyle);
    [DllImport("user32.dll")] private static extern nint GetTopWindow(nint parent);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetForegroundWindow(nint window);
}
