using Mtp.Platform.Core;

namespace Mtp.Host;

/// <summary>Orders one surface paint after brush connection and cancels work when its window closes.</summary>
internal sealed class DeferredSurfacePaint(Func<Action, bool> enqueue, Func<bool> isConnected, Func<CoreResult<bool>> paint)
{
    private bool pending;
    private bool cancelled;
    public event EventHandler? Changed;
    public bool IsPainted { get; private set; }
    public StructuredError? Error { get; private set; }

    public void Schedule()
    {
        if (cancelled || pending) return;
        pending = true;
        IsPainted = false;
        if (!enqueue(() =>
        {
            pending = false;
            if (cancelled) return;
            try
            {
                var result = isConnected() ? paint() : CoreResult<bool>.Failure(new("dock_brush_not_connected", "透明色刷尚未连接。"));
                IsPainted = result.IsSuccess && result.Value;
                Error = result.Error;
            }
            catch (Exception exception)
            {
                Error = new("dock_surface_paint_failed", "透明表面绘制失败。", exception.GetType().Name);
            }
            Changed?.Invoke(this, EventArgs.Empty);
        }))
        {
            pending = false;
            Error = new("dock_surface_queue_failed", "透明表面绘制未能进入 UI 队列。");
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    public void Cancel() => cancelled = true;
}
