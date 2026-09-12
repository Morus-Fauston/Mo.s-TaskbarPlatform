namespace Mtp.Platform.Core;

public enum TaskbarVisibility { Allowed, FullScreen, TaskbarHidden, Unknown }

/// <summary>Temporary suppression is independent of user preference and placement failures.</summary>
public static class TaskbarVisibilityPolicy
{
    public static TaskbarVisibility Evaluate(bool? fullScreen, bool? taskbarExposed) =>
        fullScreen == true ? TaskbarVisibility.FullScreen :
        taskbarExposed == false ? TaskbarVisibility.TaskbarHidden :
        fullScreen is null || taskbarExposed is null ? TaskbarVisibility.Unknown :
        TaskbarVisibility.Allowed;
}
