using System.Runtime.InteropServices;
using Mtp.Platform.Core;

namespace Mtp.Host.Flyouts;

internal enum FlyoutInputKind { MouseDown, Foreground, Focus }
internal sealed record FlyoutObservedInput(FlyoutInputKind Kind, nint Window, FlyoutNative.Point Point, uint MessageTime, long Generation);

/// <summary>Observes input without intercepting legacy delivery. One owner per Host process.</summary>
internal sealed class FlyoutInputObserver
{
    private readonly Action<FlyoutObservedInput> observe;
    private readonly Action<string> failed;
    private readonly FlyoutNative.WindowProcedure procedure;
    private readonly FlyoutNative.WinEventProcedure eventProcedure;
    private readonly string className = "MtpFlyoutInput-" + Guid.NewGuid().ToString("N");
    private readonly List<nint> hooks = [];
    private nint window, instance, buffer;
    private bool registeredClass, registeredRaw, active;
    public long Generation { get; private set; }
    public string? Error { get; private set; }
    public bool HasResources => registeredClass || registeredRaw || window != 0 || buffer != 0 || hooks.Count != 0;
    public bool IsActive => active;

    public FlyoutInputObserver(Action<FlyoutObservedInput> observe, Action<string> failed)
    {
        this.observe = observe;
        this.failed = failed;
        procedure = OnMessage;
        eventProcedure = OnEvent;
    }
    public CoreResult<bool> TryStart()
    {
        if (active) return CoreResult<bool>.Success(true);
        if (HasResources && !TryStop().IsSuccess) return Failure("旧输入订阅尚未释放");
        Generation = checked(Generation + 1);
        try
        {
            instance = FlyoutNative.GetModuleHandleW(null);
            var wc = new FlyoutNative.WindowClass { Size = (uint)Marshal.SizeOf<FlyoutNative.WindowClass>(), Procedure = procedure, Instance = instance, ClassName = className };
            if (FlyoutNative.RegisterClassExW(ref wc) == 0) throw FlyoutNative.Error("RegisterClassEx");
            registeredClass = true;
            window = FlyoutNative.CreateWindowExW(0, className, "", 0, 0, 0, 0, 0, new nint(-3), 0, instance, 0);
            if (window == 0) throw FlyoutNative.Error("CreateWindowEx");
            buffer = Marshal.AllocHGlobal(4096);
            if (!FlyoutNative.RegisterRawInputDevices([new() { Page = 1, Usage = 2, Flags = 0x100, Target = window }], 1, (uint)Marshal.SizeOf<FlyoutNative.RawDevice>()))
                throw FlyoutNative.Error("RegisterRawInputDevices");
            registeredRaw = true;
            foreach (uint eventId in new uint[] { 3, 0x8005 })
            {
                var hook = FlyoutNative.SetWinEventHook(eventId, eventId, 0, eventProcedure, 0, 0, 0);
                if (hook == 0) throw FlyoutNative.Error("SetWinEventHook");
                hooks.Add(hook);
            }
            active = true; Error = null;
            return CoreResult<bool>.Success(true);
        }
        catch (Exception error)
        {
            var message = error.Message;
            var cleanup = TryStop();
            return Failure(message + (cleanup.IsSuccess ? "" : "；" + cleanup.Error!.Message));
        }
    }
    public CoreResult<bool> TryStop()
    {
        active = false; Generation = checked(Generation + 1);
        for (int i = hooks.Count - 1; i >= 0; i--)
            if (FlyoutNative.UnhookWinEvent(hooks[i])) hooks.RemoveAt(i);
        if (registeredRaw && FlyoutNative.RegisterRawInputDevices([new() { Page = 1, Usage = 2, Flags = 1, Target = 0 }], 1, (uint)Marshal.SizeOf<FlyoutNative.RawDevice>()))
            registeredRaw = false;
        if (!registeredRaw && window != 0 && (!FlyoutNative.IsWindow(window) || FlyoutNative.DestroyWindow(window))) window = 0;
        if (window == 0 && buffer != 0) { Marshal.FreeHGlobal(buffer); buffer = 0; }
        if (window == 0 && registeredClass && FlyoutNative.UnregisterClassW(className, instance)) registeredClass = false;
        GC.KeepAlive(procedure); GC.KeepAlive(eventProcedure);
        return HasResources ? Failure("输入订阅清理未完成，保留资源供重试") : CoreResult<bool>.Success(true);
    }
    private nint OnMessage(nint hwnd, uint message, nuint wparam, nint lparam)
    {
        FlyoutObservedInput? input = null;
        if (active && message == FlyoutNative.WmInput && buffer != 0)
        {
            try
            {
                uint length = 4096;
                uint headerSize = (uint)Marshal.SizeOf<FlyoutNative.RawHeader>();
                var copied = FlyoutNative.GetRawInputData(lparam, 0x10000003, buffer, ref length, headerSize);
                if (copied == uint.MaxValue) throw FlyoutNative.Error("GetRawInputData");
                if (copied != uint.MaxValue && copied >= headerSize + Marshal.SizeOf<FlyoutNative.RawMouse>() && copied <= 4096)
                {
                    var header = Marshal.PtrToStructure<FlyoutNative.RawHeader>(buffer);
                    if (header.Type == 0)
                    {
                        var mouse = Marshal.PtrToStructure<FlyoutNative.RawMouse>(buffer + (int)headerSize);
                        // Down flags only: left/right/middle/button4/button5. Never suppress the original event.
                        if ((mouse.ButtonFlags & 0x155) != 0 && FlyoutNative.GetCursorPos(out var point))
                            input = new(FlyoutInputKind.MouseDown, FlyoutNative.WindowFromPoint(point), point,
                                unchecked((uint)FlyoutNative.GetMessageTime()), Generation);
                    }
                }
            }
            catch (Exception error) { ReportFailure(error.Message); }
        }
        // WM_INPUT's foreground cleanup must occur before the callback can close/destroy this observer.
        var result = FlyoutNative.DefWindowProcW(hwnd, message, wparam, lparam);
        if (input is not null && active && input.Generation == Generation) Deliver(input);
        return result;
    }
    private void OnEvent(nint hook, uint kind, nint hwnd, int objectId, int childId, uint thread, uint time)
    {
        if (!active || hwnd == 0 || !hooks.Contains(hook)) return;
        Deliver(new(kind == 3 ? FlyoutInputKind.Foreground : FlyoutInputKind.Focus, hwnd, default, time, Generation));
    }
    private void Deliver(FlyoutObservedInput input)
    {
        try { observe(input); }
        catch (Exception error) { ReportFailure(error.Message); }
    }
    private void ReportFailure(string message)
    {
        if (Error is not null) return;
        Error = message;
        failed(message);
    }
    private CoreResult<bool> Failure(string message)
    {
        Error = message;
        return CoreResult<bool>.Failure(new("FlyoutInputUnavailable", message));
    }
}
