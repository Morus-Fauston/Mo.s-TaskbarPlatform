namespace Mtp.Host;

public sealed record HostTestConfiguration(string Material = "none", double Alpha = 0,
    string Theme = "system", int Hertz = 0, int DurationSeconds = 30, bool Controls = false)
{
    public bool IsValid => Material is "none" or "acrylic" or "mica" &&
        Alpha is 0 or 0.5 or 1 && (Material == "none" || Alpha == 1) &&
        Theme is "system" or "light" or "dark" && Hertz is 0 or 1 or 30 or 60 &&
        DurationSeconds is >= 1 and <= 86400;
    public string CaseId => FormattableString.Invariant($"05L-{Material}-{Alpha:0.0}-{Theme}-{(Controls ? "controls" : "component")}-05K-{Hertz}Hz");
}
