# WinUI Window Regression

Run on Windows with an interactive desktop and the Host Windows App SDK prerequisites:

```powershell
powershell -ExecutionPolicy Bypass -File tests/Mtp.Host.WindowTests/Run.ps1
```

This separate process instantiates the production 05A/05D dock through reflection, shows it offscreen,
and checks native frame styles and the full client rectangle across dispatcher turns, repeated
layout, resizing, hiding, restoration, and recreation. It checks that NOACTIVATE and TOOLWINDOW
are retained, hiding preserves window identity, restoration keeps foreground focus, and repeated
layout does not raise the dock over another topmost window. A native test window emits a WinEvent
to verify the actual event subscription, UI dispatch latency, and shutdown cleanup.
The process exits nonzero on failure and writes `window-tests.log` in its isolated build output.
It also logs a read-only snapshot of the current taskbar/display environment and its read duration.
It does not launch the Host application, load preferences, modify Explorer, or close an existing
Host. Keep it separate from `dotnet test Mtp.sln`, which does not initialize a WinUI app.

These native measurements do not replace human acceptance of transparency, input, or taskbar placement.
