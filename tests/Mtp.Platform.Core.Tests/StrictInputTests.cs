using System.Text;
using Mtp.Host;

namespace Mtp.Platform.Core.Tests;

public sealed class StrictInputTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void InvalidUtf8InsideJsonStringsIsRejectedByEveryFileEntry(bool truncatedSequence)
    {
        var path = Path.GetTempFileName();
        try
        {
            var invalid = truncatedSequence ? new byte[] { 0xE4, 0xB8 } : new byte[] { 0xFF };
            File.WriteAllBytes(path, [.. Encoding.UTF8.GetBytes("{\"applicationId\":\""), .. invalid, .. Encoding.UTF8.GetBytes("\",\"featureGroups\":[]}")]);
            var declaration = new LocalJsonDeclarationSource(path).Read();
            Assert.False(declaration.IsSuccess);
            Assert.Equal("unsupported_structure", declaration.Error!.Code);

            File.WriteAllBytes(path, [.. Encoding.UTF8.GetBytes("{\"Components\":[{\"Identity\":[\""), .. invalid, .. Encoding.UTF8.GetBytes("\"],\"IsVisible\":true}]}")]);
            var preferences = new LocalComponentDisplayPreferenceStore(path).Load();
            Assert.Equal(ComponentDisplayPreferenceLoadState.Invalid, preferences.State);
            Assert.Equal("preference_invalid", preferences.Error!.Code);

            byte[] dockBytes = [.. Encoding.UTF8.GetBytes("{\"TargetDisplayId\":\""), .. invalid, .. Encoding.UTF8.GetBytes("\",\"RightGapDip\":8}")];
            File.WriteAllBytes(path, dockBytes);
            var dock = new LocalTaskbarDockPreferenceStore(path);
            Assert.Equal("dock_preference_invalid", dock.Load().Error!.Code);
            Assert.False(dock.CommitGap(12).IsSuccess);
            Assert.Equal(dockBytes, File.ReadAllBytes(path));
        }
        finally
        {
            File.Delete(path);
            File.Delete(path + ".lock");
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ValidUnicodeAndLiteralReplacementCharacterRemainValid(bool bom)
    {
        var path = Path.GetTempFileName();
        try
        {
            const string json = "{\"TargetDisplayId\":\"显示器-�\",\"RightGapDip\":8}";
            File.WriteAllText(path, json, new UTF8Encoding(bom));
            Assert.Equal(json, new LocalJsonDeclarationSource(path).Read().Value);
            Assert.Equal("显示器-�", new LocalTaskbarDockPreferenceStore(path).Load().Value!.TargetDisplayId);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
