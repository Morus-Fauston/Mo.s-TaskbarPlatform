using Mtp.Host;
using Mtp.Host.Islands;

namespace Mtp.Host.Windows.Tests;

public sealed class ContentIslandNativeTests
{
    [Fact]
    public void NativeOwnershipAndChildDestructionAreIdempotentAndNeverCloseAnotherGeneration()
    {
        var parent = NativeWindows.Create(-31000, -31000, 500, 80);
        var child = NativeWindows.Create(-31000, -31000, 240, 32);
        var ownership = NativeWindows.Ownership(child);
        try
        {
            Assert.False(NativeWindows.IsWindowVisible(child));
            NativeWindows.Attach(child, parent);
            Assert.Equal(parent, NativeWindows.GetParent(child));
            NativeWindows.Destroy(child, ownership + 1);
            Assert.True(NativeWindows.IsWindow(child));
            NativeWindows.Destroy(parent);
            Assert.False(NativeWindows.IsWindow(child));
            Assert.False(NativeWindows.HasOwnership(child, ownership));
            NativeWindows.Destroy(child, ownership);
            NativeWindows.Destroy(parent);
            var replacement = NativeWindows.Create(-31000, -31000, 240, 32);
            try
            {
                Assert.NotEqual(ownership, NativeWindows.Ownership(replacement));
                NativeWindows.Destroy(replacement, ownership);
                Assert.True(NativeWindows.IsWindow(replacement));
            }
            finally { NativeWindows.Destroy(replacement); }
        }
        finally { NativeWindows.Destroy(child, ownership); NativeWindows.Destroy(parent); }
    }

    [Fact]
    public void ProductionHostDoesNotLoadLegacyFallbackOrLabAssemblies()
    {
        Assert.DoesNotContain(typeof(MainWindow).Assembly.GetReferencedAssemblies(), name => name.Name!.Contains("Legacy") || name.Name.Contains("TaskbarIslandLab"));
        Assert.Null(typeof(MainWindow).Assembly.GetType("Mtp.Host.IndependentDockWindow"));
        Assert.Null(typeof(HostDisplayController).Assembly.GetType("Mtp.Host.HostDisplayActionController"));
    }
}
