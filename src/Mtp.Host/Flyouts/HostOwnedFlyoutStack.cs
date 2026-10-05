using System.Runtime.InteropServices;
using Mtp.Platform.Core;

namespace Mtp.Host.Flyouts;

/// <summary>Repairs relative order among explicitly supplied Host windows, without activating them.</summary>
internal static class HostOwnedFlyoutStack
{
    internal const int MaximumInputHandles = 4096;
    internal const int MaximumObservedWindows = 16384;
    private const uint PositionFlags = 0x0001 | 0x0002 | 0x0010 | 0x0200; // NOSIZE, NOMOVE, NOACTIVATE, NOOWNERZORDER

    /// <summary>
    /// Each input is ordered from low to high. Hints outrank events, which outrank taskbar groups.
    /// The last occurrence of a duplicate wins; callers put a hint's background before its foreground.
    /// Returns the number of actual native writes. Invalid input is rejected before the first write.
    /// </summary>
    internal static CoreResult<int> Apply(IEnumerable<nint> taskbar, IEnumerable<nint> events, IEnumerable<nint> hints)
    {
        if (taskbar is null || events is null || hints is null)
            return Failure("FlyoutStackInvalidInput", "浮窗叠放输入不能为空");

        var supplied = new List<nint>();
        try
        {
            foreach (var source in new[] { taskbar, events, hints })
                foreach (nint handle in source)
                {
                    if (supplied.Count == MaximumInputHandles)
                        return Failure("FlyoutStackInputBudgetExceeded", "浮窗叠放句柄超过输入预算");
                    supplied.Add(handle);
                }
        }
        catch (Exception)
        {
            return Failure("FlyoutStackInvalidInput", "浮窗叠放输入枚举失败");
        }

        var seen = new HashSet<nint>();
        var ordered = new List<nint>(supplied.Count);
        for (int index = supplied.Count - 1; index >= 0; index--)
            if (seen.Add(supplied[index])) ordered.Add(supplied[index]);
        ordered.Reverse();
        foreach (nint handle in ordered)
            if (!OwnsTopLevel(handle))
                return Failure("FlyoutStackInvalidOwner", "浮窗叠放只接受当前进程仍存活的顶层窗口");
        if (ordered.Count < 2) return CoreResult<int>.Success(0);

        // Only HWND links are observed outside the supplied set. No external process,
        // window content, owner state, or application protocol is inspected or modified.
        var ranks = new Dictionary<nint, int>(ordered.Count);
        var observed = new HashSet<nint>();
        nint current = GetTopWindow(0);
        for (int rank = 0; current != 0 && rank < MaximumObservedWindows; rank++)
        {
            if (!observed.Add(current)) break;
            if (seen.Contains(current)) ranks.Add(current, rank);
            if (ranks.Count == ordered.Count) break;
            current = FlyoutNative.GetWindow(current, 2); // GW_HWNDNEXT
        }
        if (ranks.Count != ordered.Count)
            return Failure("FlyoutStackObservationIncomplete", "未在有界原生顺序中找到全部浮窗");

        int writes = 0, previousRank = int.MaxValue;
        foreach (nint handle in ordered)
        {
            int rank = ranks[handle];
            if (rank >= previousRank)
            {
                // Revalidate immediately before a mutation; a disappeared/reused HWND
                // must never cause a write to a window now owned by another process.
                if (!OwnsTopLevel(handle))
                    return Failure("FlyoutStackOwnerChanged", "浮窗身份在叠放应用前已失效");
                if (!FlyoutNative.SetWindowPos(handle, new nint(-1), 0, 0, 0, 0, PositionFlags))
                    return Failure("FlyoutStackApplyFailed", "自有浮窗原生叠放失败");
                rank = -++writes;
            }
            previousRank = rank;
        }
        return CoreResult<int>.Success(writes);
    }

    private static bool OwnsTopLevel(nint handle) => handle != 0 && FlyoutNative.IsWindow(handle) &&
        FlyoutNative.GetWindowThreadProcessId(handle, out uint process) != 0 && process == (uint)Environment.ProcessId &&
        FlyoutNative.Root(handle) == handle;

    private static CoreResult<int> Failure(string code, string message) => CoreResult<int>.Failure(new(code, message));

    [DllImport("user32.dll")] private static extern nint GetTopWindow(nint parent);
}
