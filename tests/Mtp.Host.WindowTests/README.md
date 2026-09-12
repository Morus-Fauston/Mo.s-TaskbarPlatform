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

## Optional modes

Both run from the same output directory and exit nonzero on failure.

```powershell
# Transparency contract guard: a top-level MTP window over the taskbar must let the taskbar
# show through after frame/DWM preparation + alpha brush + one GDI surface paint. Also measures
# the embedded child case on both taskbars for comparison and logs it without asserting it
# (an embedded WS_CHILD window cannot host the DWM frame extension, so it stays opaque; see
# Docs/Research/SYS-003). Writes taskbar-surface.log.
Mtp.Host.WindowTests.exe --taskbar-surface

# Child material fixture: binds a probe window to a fixture parent and checks brush connection
# plus the surface paint. Writes child-material.log.
Mtp.Host.WindowTests.exe --child-material [--child-pixels]
```

The `--taskbar-surface` guard needs a visible taskbar and an interactive desktop; it will fail
if the taskbar is hidden or the session is not interactive. Removing the `TryPrepareControlWindowSurface`
call from the top-level path is what its red run exercises.

These native measurements do not replace human acceptance of transparency, input, or taskbar placement.
