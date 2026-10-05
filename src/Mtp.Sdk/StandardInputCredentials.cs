using System.IO.Pipes;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Mtp.Sdk;

/// <summary>Wraps inherited stdin with PipeStream's cancellable reads; it does not own the process handle.</summary>
internal static class StandardInputCredentials
{
    public static Stream Open()
    {
        if (!OperatingSystem.IsWindows()) return Console.OpenStandardInput();
        var handle = GetStdHandle(-10);
        if (handle == IntPtr.Zero || handle == new IntPtr(-1)) throw new IOException("StandardInputUnavailable");
        return new AnonymousPipeClientStream(PipeDirection.In, new SafePipeHandle(handle, ownsHandle: false));
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetStdHandle(int identifier);
}
