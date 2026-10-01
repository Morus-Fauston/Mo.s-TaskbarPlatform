using System.Reflection;
using System.Runtime.Versioning;
using Mtp.Host;

namespace Mtp.Platform.Core.Tests;

public sealed class HostRuntimeArchitectureTests
{
    [Fact]
    public void RuntimeAndPureTestsHaveNoWindowsToolchainDependency()
    {
        foreach (var assembly in new[] { typeof(DeclarationValidator).Assembly, GetType().Assembly })
        {
            Assert.Equal(".NETCoreApp,Version=v10.0", assembly.GetCustomAttribute<TargetFrameworkAttribute>()!.FrameworkName);
            Assert.Null(assembly.GetCustomAttribute<TargetPlatformAttribute>());
            Assert.DoesNotContain(assembly.GetReferencedAssemblies(), reference =>
                reference.Name is "Mtp.Host" or "Microsoft.Windows.SDK.NET" or "WinRT.Runtime" ||
                reference.Name!.StartsWith("Microsoft.UI", StringComparison.Ordinal) ||
                reference.Name.StartsWith("Microsoft.WinUI", StringComparison.Ordinal));
        }
    }
}
