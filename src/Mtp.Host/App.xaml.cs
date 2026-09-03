using Microsoft.UI.Xaml;
using System.IO;

namespace Mtp.Host;

/// <summary>
/// Starts the ordinary WinUI window used by the first Host display slice.
/// </summary>
public partial class App : Application
{
    private Window? window;

    public App()
    {
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        var declarationPath = Path.Combine(AppContext.BaseDirectory, "declaration.json");
        var loader = new HostDeclarationLoader(new LocalJsonDeclarationSource(declarationPath));
        window = new MainWindow(loader.Load());
        window.Activate();
    }
}
