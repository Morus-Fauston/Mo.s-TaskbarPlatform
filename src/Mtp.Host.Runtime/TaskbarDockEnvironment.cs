using Mtp.Platform.Core;

namespace Mtp.Host;

internal sealed record TaskbarDockEnvironmentSnapshot(
    string DisplayId, PixelRect DisplayBounds, PixelRect WorkArea, uint Dpi,
    string? TaskbarIdentity, TaskbarDockGeometry? Geometry, StructuredError? Error, TaskbarVisibility Visibility);

internal interface ITaskbarDockEnvironment
{
    CoreResult<TaskbarDockEnvironmentSnapshot> Capture(string? displayId);
}
