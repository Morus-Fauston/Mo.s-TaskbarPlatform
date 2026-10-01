using System.Diagnostics;
using System.IO;
using System.Threading;

namespace Mtp.Host;

/// <summary>A persistent sidecar avoids directory-alias and Windows session mutex splits.</summary>
internal static class PreferenceFileLock
{
    public static FileStream Acquire(string fullPath, TimeSpan timeout)
    {
        var elapsed = Stopwatch.StartNew();
        while (true)
        {
            try
            {
                return new FileStream(fullPath + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException exception) when ((exception.HResult & 0xffff) is 11 or 32 or 33 && elapsed.Elapsed < timeout)
            {
                Thread.Sleep(20);
            }
        }
    }
}
