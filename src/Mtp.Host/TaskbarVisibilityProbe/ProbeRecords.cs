using Mtp.Platform.Core;

namespace Mtp.Host.Experiments;

public sealed record ProbeTarget(string DisplayId, PixelRect Bounds, uint Dpi, long Taskbar);
public sealed record ProbeWindowInfo(long Handle, bool Exists, uint Style, uint ExStyle,
    bool LogicallyVisible, PixelRect? Bounds, uint ProcessId, uint ThreadId);
public sealed record ProbeClipReading(int RegionType, PixelRect? Bounds, ProbeClip? Exposure);
public sealed record ProbeTrace(long Sequence, long Timestamp, string Source, string DisplayId,
    long Window, uint Code, long WParam, long LParam, uint SourceTimeMs);
public sealed record ProbeSnapshot(
    ProbeTarget Target, long Generation, long StartedAt, long CompletedAt,
    ProbeWindowInfo Taskbar, ProbeWindowInfo Probe, IReadOnlyList<ProbeWindowInfo> ParentChain,
    byte? Alpha, uint? LayeredFlags, uint? Cloaked,
    ProbeClipReading DefaultClip, ProbeClipReading SiblingClip,
    ProbeSignals Signals, ProbeExposure Exposure, string Diagnostic);
