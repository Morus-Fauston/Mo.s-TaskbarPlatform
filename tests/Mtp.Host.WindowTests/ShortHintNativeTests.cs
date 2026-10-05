using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Mtp.Host.Flyouts;
using Mtp.Platform.Core;

namespace Mtp.Host.WindowTests;

/// <summary>Runs on the WinUI dispatcher; the caller serializes use of the desktop.</summary>
internal static class ShortHintNativeTests
{
    internal static async Task RunAsync(Action<string> log)
    {
        int closed = 0;
        var hint = new ShortHintWindow(new TextBlock { Text = "普通提示：输入穿透", Margin = new Thickness(12) },
            () => closed++, (_, _) => throw new IOException("Injected diagnostic sink failure"));
        nint handle = hint.Handle;
        nint foreground = FlyoutNative.GetForegroundWindow();
        try
        {
            var work = Microsoft.UI.Windowing.DisplayArea.Primary.WorkArea;
            var bounds = new PixelRect(work.X + 32, work.Y + 32, 320, 64);
            hint.Apply(bounds, 0);
            hint.Show();
            Check(!hint.IsVisible, "Zero-opacity window was reported actually visible.");
            hint.Apply(bounds, 0.001);
            Check(!hint.IsVisible, "A positive opacity rounded to native alpha zero started visible lifetime.");
            hint.Apply(bounds, 1);
            await Task.Delay(100);
            Check(hint.IsAlive && hint.IsVisible && hint.LastBounds == FlyoutNative.Bounds(handle),
                "Hint did not expose its actual visible native bounds.");
            Check(hint.LastBounds == bounds, "Native position differs from the requested pixel bounds.");
            Check(FlyoutNative.GetForegroundWindow() == foreground, "Automatic hint display stole foreground.");
            long style = FlyoutNative.GetWindowLongPtrW(handle, -20).ToInt64();
            Check((style & 0x080800A0) == 0x080800A0,
                "Hint must be layered, transparent, no-activate and a tool window.");
            Check(!hint.RootForTesting.IsHitTestVisible, "Ordinary hint content accepted pointer input.");
            await ConsoleScreenshot.SaveAsync(hint.RootForTesting, Path.Combine(AppContext.BaseDirectory, "hint-content.png"));
            await TaskbarFlyoutNativeRegression.SaveNativeAsync(handle, Path.Combine(AppContext.BaseDirectory, "hint-native.png"));
            log("PASS: ordinary hint native bounds, visible opacity, no activation, transparent layered tool-window styles and diagnostic isolation.");

            int attempts = 0;
            hint.CloseWindowForTesting = value =>
            {
                if (++attempts == 1) throw new COMException("Injected native Close failure.");
                value.Close();
            };
            Check(!hint.TryClose().IsSuccess && hint.IsAlive && hint.Handle == handle,
                "Failed Close discarded the owned live HWND.");
            Check(hint.TryClose().IsSuccess && !hint.IsAlive && !FlyoutNative.IsWindow(handle) && attempts == 2,
                "Explicit close retry did not release the owned HWND.");
            Check(hint.TryClose().IsSuccess && closed == 1, "Repeated cleanup repeated the close callback.");
            log("PASS: ordinary hint Close failure retains ownership, explicit retry succeeds, cleanup is idempotent.");
        }
        finally
        {
            hint.CloseWindowForTesting = null;
            Check(hint.TryClose().IsSuccess, "Ordinary hint fixture cleanup failed.");
        }
    }

    /// <summary>The supplied callback sends real input and checks delivery in a separately owned process.</summary>
    internal static async Task VerifyCrossProcessPassThroughAsync(nint ownedTarget, PixelRect bounds,
        Func<Task> clickAndVerifyExactlyOnce, Action<string> log)
    {
        FlyoutNative.GetWindowThreadProcessId(ownedTarget, out uint targetProcess);
        Check(targetProcess != 0 && targetProcess != Environment.ProcessId && FlyoutNative.IsWindow(ownedTarget),
            "Cross-process fixture requires an existing separately owned target.");
        var hint = new ShortHintWindow(new TextBlock { Text = "指针穿透到另一进程", Margin = new Thickness(12) },
            () => { }, (_, _) => { });
        nint foreground = FlyoutNative.GetForegroundWindow();
        try
        {
            hint.Apply(bounds, 1);
            hint.Show();
            await Task.Delay(100);
            Check(FlyoutNative.GetForegroundWindow() == foreground, "Hint changed foreground before cross-process input.");
            var hit = FlyoutNative.WindowFromPoint(new() { X = bounds.X + bounds.Width / 2, Y = bounds.Y + bounds.Height / 2 });
            Check(FlyoutNative.Root(hit) == FlyoutNative.Root(ownedTarget),
                "Hint does not pass hit testing through to the separately owned target.");
            await clickAndVerifyExactlyOnce();
            log("PASS: ordinary hint passes a real click exactly once to a separately owned process.");
        }
        finally { Check(hint.TryClose().IsSuccess, "Cross-process hint cleanup failed."); }
    }

    private static void Check(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }
}
