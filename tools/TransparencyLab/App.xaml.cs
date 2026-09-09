using Microsoft.UI.Xaml;

namespace TransparencyLab;

public partial class App : Application
{
    private LabWindow? window;

    public App()
    {
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        window = new LabWindow();
        window.Configure();
        window.Activate();
    }
}