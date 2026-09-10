using System.Xml.Linq;

namespace Mtp.Platform.Core.Tests;

public sealed class HostErrorPresentationTests
{
    [Fact]
    public void HostErrorRemainsOutsideTheComponentVisibilityContainer()
    {
        var document = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "MainWindow.xaml"));
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var errorText = document
            .Descendants()
            .Single(element => (string?)element.Attribute(x + "Name") == "ErrorText");

        Assert.DoesNotContain(
            errorText.Ancestors(),
            element => (string?)element.Attribute(x + "Name") == "ComponentCard");
        Assert.Equal("True", (string?)errorText.Attribute("IsTextSelectionEnabled"));
        Assert.Equal("WrapWholeWords", (string?)errorText.Attribute("TextWrapping"));
    }
}
