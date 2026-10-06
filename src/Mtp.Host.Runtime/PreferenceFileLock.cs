using System.Diagnostics;
using System.IO;
using System.Threading;

namespace Mtp.Host;

/// <summary>A persistent sidecar avoids directory-alias and Windows session mutex splits.</summary>
internal static class PreferenceFileLock
{
    public static void Replace(string temporaryPath, string destinationPath)
    {
        var elapsed = Stopwatch.StartNew();
        while (true)
        {
            try
            {
                if (File.Exists(destinationPath)) File.Replace(temporaryPath, destinationPath, null);
                else File.Move(temporaryPath, destinationPath);
                return;
            }
            // ReplaceFile can report ERROR_UNABLE_TO_REMOVE_REPLACED (1175) when
            // a reader arrives after it opens the destination. The old file remains.
            catch (IOException exception) when ((exception.HResult & 0xffff) is 32 or 33 or 1175 &&
                File.Exists(temporaryPath) && File.Exists(destinationPath) && elapsed.Elapsed < TimeSpan.FromSeconds(2))
            {
                Thread.Sleep(20);
            }
        }
    }

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
