using Mtp.Host.Islands;
using Mtp.Platform.Core;

namespace Mtp.Host.WindowTests;

internal sealed class OwnedIslandTarget : IDisposable
{
    public nint Parent { get; private set; }
    private int epoch;
    public OwnedIslandTarget() => Recreate();
    public void Destroy() { NativeWindows.Destroy(Parent); Parent = 0; }
    public void Recreate()
    {
        Destroy();
        Parent = NativeWindows.Create(-30000, -29280, 1000, 80);
        NativeWindows.Show(Parent, true);
        epoch++;
    }
    public CoreResult<IslandTarget> Capture(TaskbarDockPreferences preferences)
    {
        if (Parent == 0) return CoreResult<IslandTarget>.Failure(new("fixture_parent_missing", "自有父窗口已销毁。"));
        return CoreResult<IslandTarget>.Success(new(Parent, "owned-" + epoch, new(
            preferences.TargetDisplayId ?? "owned-primary", new(-30000, -30000, 1000, 800),
            new(-30000, -29280, 1000, 80), new(-29200, -29280, 200, 80), NativeWindows.GetDpiForWindow(Parent)), true));
    }
    public void Dispose() => Destroy();
}
