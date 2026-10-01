using System.Diagnostics;
using Mtp.Host;
using Mtp.Platform.Core;

if (args.Length != 5) return 2;
var mode = args[0];
var path = args[1];
var prefix = args[2];
var ready = args[3];
var gate = args[4];
using var heldLock = mode == "hold"
    ? new FileStream(path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None)
    : null;
File.WriteAllText(ready, "ready");
var timeout = Stopwatch.StartNew();
while (!File.Exists(gate))
{
    if (timeout.Elapsed > TimeSpan.FromSeconds(30)) return 3;
    Thread.Sleep(10);
}
if (mode == "hold") return 0;
var store = new LocalComponentDisplayPreferenceStore(path);
for (var index = 0; index < 40; index++)
{
    var identity = new StableIdentity(new StableId("app")).CreateChild(new StableId(prefix + index));
    var result = store.CommitVisibility(identity, true);
    if (!result.IsSuccess)
    {
        Console.Error.WriteLine(result.Error);
        return 4;
    }
}
return 0;
