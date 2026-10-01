using System.Globalization;

namespace TaskbarIslandLab.Logic;

public sealed record LabOptions
{
    public string Mode { get; init; } = "owned";
    public string Scenario { get; init; } = "verify";
    public string? Monitor { get; init; }
    public long ParentHwnd { get; init; }
    public string Material { get; init; } = "none";
    public double Alpha { get; init; } = 0.5;
    public string Theme { get; init; } = "system";
    public int Hz { get; init; } = 30;
    public int VisibleSeconds { get; init; } = 600;
    public int HiddenSeconds { get; init; } = 20;
    public int TimeoutSeconds { get; init; } = 45;
    public int X { get; init; } = 80;
    public int Y { get; init; } = 80;
    public bool Diagnostics { get; init; }
    public string Output { get; init; } = Path.GetFullPath(".scratch/二期开发/verification/05F/run-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture));
    public string StopFile => Path.Combine(Output, "stop.request");
    public double MeasureSeconds => 5 + 10 + VisibleSeconds + HiddenSeconds + 10;

    public static LabOptions Parse(string[] args)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 0; i < args.Length; i += 2)
            if (i + 1 >= args.Length || !args[i].StartsWith("--", StringComparison.Ordinal) || !values.TryAdd(args[i][2..], args[i + 1]))
                throw new ArgumentException("Options must be unique --name value pairs.");
        string Get(string key, string fallback) => values.GetValueOrDefault(key, fallback);
        int Number(string key, int fallback, int min, int max)
        {
            if (!int.TryParse(Get(key, fallback.ToString(CultureInfo.InvariantCulture)), CultureInfo.InvariantCulture, out var n) || n < min || n > max)
                throw new ArgumentException($"Invalid --{key} ({min}..{max}).");
            return n;
        }
        string Choice(string key, string fallback, params string[] choices)
        {
            var value = Get(key, fallback);
            return choices.Contains(value) ? value : throw new ArgumentException($"Invalid --{key}.");
        }
        string[] known = ["mode", "scenario", "monitor", "parent-hwnd", "material", "alpha", "theme", "hz", "visible-seconds", "hidden-seconds", "timeout-seconds", "x", "y", "diagnostics", "output"];
        if (values.Keys.Any(key => !known.Contains(key))) throw new ArgumentException("Unknown option.");
        var mode = Choice("mode", "owned", "owned", "top-level", "empty", "explorer");
        var scenario = Choice("scenario", "verify", "verify", "measure", "manual", "list-targets");
        var parent = Get("parent-hwnd", "0");
        if (!long.TryParse(parent.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? parent[2..] : parent,
            parent.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? NumberStyles.HexNumber : NumberStyles.Integer,
            CultureInfo.InvariantCulture, out var hwnd) || hwnd < 0) throw new ArgumentException("Invalid HWND.");
        var monitor = values.GetValueOrDefault("monitor");
        if (mode == "explorer")
        {
            if (scenario == "verify" || (scenario != "list-targets" && (string.IsNullOrWhiteSpace(monitor) || hwnd == 0)))
                throw new ArgumentException("Explorer requires explicit --scenario manual/measure, --monitor and --parent-hwnd. Use --scenario list-targets to inspect candidates.");
        }
        else if (hwnd != 0 || monitor != null || scenario == "list-targets")
            throw new ArgumentException("Explorer selection is allowed only in explicit explorer mode.");
        if (scenario == "verify" && mode != "owned") throw new ArgumentException("Verification only uses owned windows.");
        if (!double.TryParse(Get("alpha", "0.5"), CultureInfo.InvariantCulture, out var alpha) || (alpha != 0 && alpha != 0.5 && alpha != 1))
            throw new ArgumentException("Alpha must be 0, 0.5 or 1.");
        var hz = Number("hz", 30, 0, 60);
        if (hz is not (0 or 1 or 30 or 60)) throw new ArgumentException("Hz must be 0, 1, 30 or 60.");
        var options = new LabOptions
        {
            Mode = mode,
            Scenario = scenario,
            Monitor = monitor,
            ParentHwnd = hwnd,
            Material = Choice("material", "none", "none", "acrylic", "mica"),
            Alpha = alpha,
            Theme = Choice("theme", "system", "system", "light", "dark"),
            Hz = hz,
            VisibleSeconds = Number("visible-seconds", 600, 1, 3600),
            HiddenSeconds = Number("hidden-seconds", 20, 1, 600),
            TimeoutSeconds = Number("timeout-seconds", scenario == "manual" ? 1800 : 45, 5, 7200),
            X = Number("x", 80, -32000, 32000),
            Y = Number("y", 80, -32000, 32000),
            Diagnostics = Choice("diagnostics", "off", "on", "off") == "on"
        };
        return values.TryGetValue("output", out var output) ? options with { Output = Path.GetFullPath(output) } : options;
    }
}
