using Microsoft.UI.Xaml;
using System.Linq;
using System.Text;
using Mtp.Platform.Core;

namespace Mtp.Host;

/// <summary>
/// The ordinary Host window for the minimum display baseline.
/// </summary>
public sealed partial class MainWindow : Window
{
    private readonly HostDisplayController displayController;
    private readonly IndependentDockWindowController dockWindowController;
    private readonly ExplorerTaskbarProbeController probeController;
    private HostComponentDisplayModel? selectedComponent;
    private ExplorerTaskbarProbeWindow? topLevelControlWindow;
    private bool applyingVisibility;

    public MainWindow(
        HostDisplayController displayController,
        HostDisplayLoadResult displayLoad,
        IndependentDockWindowController dockWindowController,
        ExplorerTaskbarProbeController probeController)
    {
        this.displayController = displayController ?? throw new ArgumentNullException(nameof(displayController));
        ArgumentNullException.ThrowIfNull(displayLoad);
        this.dockWindowController = dockWindowController ?? throw new ArgumentNullException(nameof(dockWindowController));
        this.probeController = probeController ?? throw new ArgumentNullException(nameof(probeController));
        InitializeComponent();
        Closed += MainWindow_Closed;
        this.probeController.StateChanged += ProbeController_StateChanged;

        AppWindow.Resize(new Windows.Graphics.SizeInt32(560, 460));

        selectedComponent = displayLoad.Components.FirstOrDefault();
        if (selectedComponent is not null)
        {
            ApplyDisplay(selectedComponent);
            VisibilityToggle.IsEnabled = true;
            applyingVisibility = true;
            VisibilityToggle.IsOn = selectedComponent.IsVisible;
            applyingVisibility = false;
        }
        else
        {
            var fallback = new Component(
                new StableIdentity(new StableId("mtp"))
                    .CreateChild(new StableId("declaration-error")),
                CapabilityState.Failed(displayLoad.DeclarationError?.Message ?? "声明未加载。"));
            ApplyDisplay(HostComponentDisplayModel.From(fallback));
        }

        UpdateProbeButtons();

        var error = displayLoad.DeclarationError ?? displayLoad.PreferenceError;
        if (error is not null)
        {
            ErrorText.Text = FormatError(error);
            ErrorText.Visibility = Visibility.Visible;
        }
    }

    private void ApplyDisplay(HostComponentDisplayModel display)
    {
        ComponentText.Text = display.Text;
        IdentityText.Text = $"声明组件：{string.Join(" / ", display.Identity.Segments.Select(segment => segment.Value))}";
        StatusText.Text = display.StatusLabel;
        ComponentCard.Visibility = display.IsVisible ? Visibility.Visible : Visibility.Collapsed;
    }

    private void VisibilityToggle_Toggled(object sender, RoutedEventArgs args)
    {
        if (applyingVisibility || selectedComponent is null)
        {
            return;
        }

        var result = displayController.SetVisibility(selectedComponent.Identity, VisibilityToggle.IsOn);
        if (result.IsSuccess)
        {
            selectedComponent = result.Value!;
            ApplyDisplay(selectedComponent);
            if (selectedComponent.IsVisible)
            {
                var showResult = dockWindowController.Show(selectedComponent);
                if (!showResult.IsSuccess)
                {
                    ShowHostError(showResult.Error!);
                }
            }
            else
            {
                // The component is already hidden, so detaching the probe does not reopen the dock window.
                var detachResult = probeController.Detach();
                if (!detachResult.IsSuccess)
                {
                    ShowHostError(detachResult.Error!);
                }

                var closeResult = dockWindowController.Close();
                if (!closeResult.IsSuccess)
                {
                    ShowHostError(closeResult.Error!);
                }
            }

            UpdateProbeButtons();
            return;
        }

        applyingVisibility = true;
        VisibilityToggle.IsOn = selectedComponent.IsVisible;
        applyingVisibility = false;
        ErrorText.Text = FormatError(result.Error!);
        ErrorText.Visibility = Visibility.Visible;
    }

    private void RunProbeButton_Click(object sender, RoutedEventArgs args)
    {
        if (selectedComponent is null)
        {
            return;
        }

        var outcome = probeController.Run(
            selectedComponent,
            new ExplorerTaskbarProbeRequest(SimulateFailureCheck.IsChecked == true, SelectedTransparencyMode));
        ShowProbeOutcome(outcome);
        UpdateProbeButtons();
    }

    private void StopProbeButton_Click(object sender, RoutedEventArgs args)
    {
        var result = probeController.Detach();
        if (!result.IsSuccess)
        {
            ShowHostError(result.Error!);
        }
        else
        {
            ProbeStatusText.Text = "探针已停止，已恢复独立贴靠窗口。";
            ProbeStatusText.Visibility = Visibility.Visible;
        }

        UpdateProbeButtons();
    }

    /// <summary>
    /// Control experiment for the transparency question: the same probe window shown as a top-level
    /// window, never reparented. Clicking again closes it.
    /// </summary>
    private void TopLevelControlButton_Click(object sender, RoutedEventArgs args)
    {
        if (topLevelControlWindow is not null)
        {
            CloseTopLevelControlWindow();
            TopLevelControlButton.Content = "显示透明顶级对照窗口";
            return;
        }

        var display = selectedComponent ?? HostComponentDisplayModel.From(new Component(
            new StableIdentity(new StableId("mtp")).CreateChild(new StableId("transparency-control")),
            CapabilityState.Available));
        var controlWindow = new ExplorerTaskbarProbeWindow(display);
        controlWindow.PrepareHidden(new Windows.Graphics.SizeInt32(360, 60), useToolWindowPresenter: false);
        controlWindow.PrepareAsTopLevelControl();
        var applied = Win32ExplorerTaskbarEmbedAdapter.TryApplyWindowTransparency(controlWindow, SelectedTransparencyMode, out var transparencyDetail);
        controlWindow.Closed += TopLevelControlWindow_Closed;
        var position = AppWindow.Position;
        controlWindow.ShowAt(new Windows.Graphics.PointInt32(position.X + 24, position.Y + AppWindow.Size.Height - 90));
        // TransparencyLab only re-extends the DWM frame after load; it does not re-strip styles after showing.
        var reapplied = $"style=0x{(uint)(long)0:X8} (no post-show restrip; Lab parity)";
        topLevelControlWindow = controlWindow;
        TopLevelControlButton.Content = "关闭透明顶级对照窗口";

        controlStatusBase = applied
            ? $"顶级对照窗口已显示（未嵌入任务栏）。apply_transparency：{transparencyDetail}；after_show：{reapplied}；backdrop_state：{controlWindow.DescribeBackdropState()}。请观察其背景是否透明并与嵌入探针对比。"
            : $"顶级对照窗口已显示，但透明模式应用失败：{transparencyDetail}";
        controlWindow.AppWindow.Changed += TopLevelControlWindow_Changed;
        UpdateControlDisplayEnvironment(controlWindow);
    }

    private string controlStatusBase = string.Empty;

    /// <summary>
    /// The display environment is re-read whenever the control window moves, so the maintainer can drag it
    /// between monitors and read the monitor line for the screen it is actually on.
    /// </summary>
    private void TopLevelControlWindow_Changed(Microsoft.UI.Windowing.AppWindow sender, Microsoft.UI.Windowing.AppWindowChangedEventArgs args)
    {
        if (args.DidPositionChange && topLevelControlWindow is not null)
        {
            UpdateControlDisplayEnvironment(topLevelControlWindow);
        }
    }

    private void UpdateControlDisplayEnvironment(ExplorerTaskbarProbeWindow controlWindow)
    {
        ProbeStatusText.Text = $"{controlStatusBase}\n当前所在显示器：{Win32ExplorerTaskbarEmbedAdapter.DescribeDisplayEnvironment(controlWindow.WindowHandle)}";
        ProbeStatusText.Visibility = Visibility.Visible;
    }

    private ProbeTransparencyMode SelectedTransparencyMode => TransparencyModeCombo.SelectedIndex switch
    {
        1 => ProbeTransparencyMode.TintDiagnostic,
        2 => ProbeTransparencyMode.AcrylicController,
        3 => ProbeTransparencyMode.NearTransparentBackdrop,
        _ => ProbeTransparencyMode.Backdrop,
    };

    private void TopLevelControlWindow_Closed(object sender, WindowEventArgs args)
    {
        if (topLevelControlWindow is not null)
        {
            topLevelControlWindow.Closed -= TopLevelControlWindow_Closed;
            topLevelControlWindow = null;
        }

        TopLevelControlButton.Content = "显示透明顶级对照窗口";
    }

    private void CloseTopLevelControlWindow()
    {
        var controlWindow = topLevelControlWindow;
        if (controlWindow is null)
        {
            return;
        }

        controlWindow.Closed -= TopLevelControlWindow_Closed;
        try
        {
            controlWindow.AppWindow.Changed -= TopLevelControlWindow_Changed;
        }
        catch (Exception)
        {
            // The AppWindow may already be gone.
        }

        topLevelControlWindow = null;
        try
        {
            controlWindow.Close();
        }
        catch (Exception)
        {
            // The control window is diagnostic only; a close failure must not block the Host.
        }
    }

    private void ProbeController_StateChanged(object? sender, EventArgs args)
    {
        var state = probeController.State;
        if (!state.IsEmbedded && state.Error is { Code: "explorer_probe_host_window_lost" } lostError)
        {
            ProbeStatusText.Text = $"{ExplorerTaskbarProbeOutcome.FallbackMessage}\n{FormatError(lostError)}";
            ProbeStatusText.Visibility = Visibility.Visible;
        }

        UpdateProbeButtons();
    }

    private void ShowProbeOutcome(ExplorerTaskbarProbeOutcome outcome)
    {
        if (outcome.Succeeded && outcome.Report is not null)
        {
            ProbeStatusText.Text = ExplorerTaskbarProbeReportFormatter.Format(outcome.Report);
        }
        else
        {
            var builder = new StringBuilder();
            builder.AppendLine(ExplorerTaskbarProbeOutcome.FallbackMessage);
            if (outcome.Error is not null)
            {
                builder.AppendLine(FormatError(outcome.Error));
            }

            if (outcome.FallbackError is not null)
            {
                builder.AppendLine($"独立贴靠窗口恢复失败：{FormatError(outcome.FallbackError)}");
            }

            ProbeStatusText.Text = builder.ToString().TrimEnd();
        }

        ProbeStatusText.Visibility = Visibility.Visible;
    }

    private void UpdateProbeButtons()
    {
        var canProbe = selectedComponent is { IsVisible: true };
        RunProbeButton.IsEnabled = canProbe;
        StopProbeButton.IsEnabled = probeController.State.IsEmbedded;
    }

    public void ShowHostError(StructuredError error)
    {
        ArgumentNullException.ThrowIfNull(error);
        ErrorText.Text = FormatError(error);
        ErrorText.Visibility = Visibility.Visible;
    }

    private void MainWindow_Closed(object sender, WindowEventArgs args)
    {
        probeController.StateChanged -= ProbeController_StateChanged;
        CloseTopLevelControlWindow();
        _ = probeController.Detach();
        probeController.Dispose();
        _ = dockWindowController.Close();
        dockWindowController.Dispose();
    }

    private static string FormatError(StructuredError error) =>
        string.IsNullOrWhiteSpace(error.Path)
            ? $"{error.Code}: {error.Message}"
            : $"{error.Code}: {error.Message} ({error.Path})";
}
