using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;

namespace Mtp.Host;

internal static class HostEvidenceIdentity
{
    public static object Capture(string target)
    {
        string Hash(string path) => File.Exists(path) ? Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))) : "unavailable";
        var dll = typeof(MainWindow).Assembly.Location;
        var exe = Path.Combine(Path.GetDirectoryName(dll)!, "Mtp.Host.exe");
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !Directory.Exists(Path.Combine(root.FullName, ".git")) && !File.Exists(Path.Combine(root.FullName, ".git"))) root = root.Parent;
        string Git(params string[] arguments)
        {
            if (root is null) return "unavailable: source checkout not found";
            try
            {
                using var process = new Process { StartInfo = new("git") { WorkingDirectory = root.FullName, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true } };
                foreach (var argument in arguments) process.StartInfo.ArgumentList.Add(argument);
                process.Start();
                var output = process.StandardOutput.ReadToEndAsync();
                var error = process.StandardError.ReadToEndAsync();
                if (!process.WaitForExit(3000)) { process.Kill(); return "unavailable: git timed out"; }
                return process.ExitCode == 0 ? output.GetAwaiter().GetResult() : "unavailable: " + error.GetAwaiter().GetResult();
            }
            catch (Exception error) { return "unavailable: " + error.Message; }
        }
        return new
        {
            HostDll = dll,
            HostDllSha256 = Hash(dll),
            HostExe = exe,
            HostExeSha256 = Hash(exe),
            RuntimeSha256 = Hash(typeof(HostDisplayController).Assembly.Location),
            ProcessExecutable = Environment.ProcessPath,
            ProcessExecutableSha256 = Hash(Environment.ProcessPath!),
            Environment.ProcessId,
            Windows = Environment.OSVersion.ToString(),
            Target = target,
            Baseline = Git("rev-parse", "HEAD"),
            UncommittedScope = Git("status", "--porcelain=v1"),
            TrackedDiffSha256 = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(Git("diff", "HEAD", "--")))),
            BinaryTime = File.GetLastWriteTimeUtc(dll),
            CapturedAt = DateTimeOffset.UtcNow,
            EvidenceKind = Path.GetFileName(Environment.ProcessPath) == "Mtp.Host.exe" ? "Host" : "automated-owned-parent-fixture"
        };
    }
}
