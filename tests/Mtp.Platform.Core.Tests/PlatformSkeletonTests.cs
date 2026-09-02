using Mtp.Contracts;
using Mtp.Platform.Core;

namespace Mtp.Platform.Core.Tests;

public sealed class PlatformSkeletonTests
{
    [Fact]
    public void CoreAssemblyHasNoWindowsOrStorageDependencies()
    {
        var referencedAssemblyNames = typeof(PlatformCoreMarker)
            .Assembly
            .GetReferencedAssemblies()
            .Select(assembly => assembly.Name)
            .Where(name => name is not null)
            .Cast<string>()
            .ToArray();

        var forbiddenPrefixes = new[]
        {
            "Microsoft.UI",
            "Microsoft.WindowsAppSDK",
            "Microsoft.Data.Sqlite",
            "System.Data.SQLite",
            "System.IO.Pipes",
            "WindowsBase",
        };

        Assert.DoesNotContain(
            referencedAssemblyNames,
            name => forbiddenPrefixes.Any(prefix => name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public void CoreAndContractsDoNotDependOnHostOrEachOther()
    {
        var coreReferences = typeof(PlatformCoreMarker).Assembly.GetReferencedAssemblies();
        var contractReferences = typeof(ContractAssemblyMarker).Assembly.GetReferencedAssemblies();

        Assert.DoesNotContain(coreReferences, reference => reference.Name is "Mtp.Host" or "Mtp.Contracts");
        Assert.DoesNotContain(contractReferences, reference => reference.Name is "Mtp.Host" or "Mtp.Platform.Core");
    }
}
