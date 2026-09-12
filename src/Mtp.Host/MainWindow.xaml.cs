using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Windowing;
using System.Linq;
using System.Text;
using Mtp.Platform.Core;

namespace Mtp.Host;

/// <summary>
/// The ordinary Host window for the minimum display baseline.
/// </summary>
public sealed partial class MainWindow : Window
{
    private readonly HostDisplayActionController displayActions;
    private readonly TopLevelControlWindowOwner topLevelControlWindowOwner = new();
    private HostComponentDisplayModel? selectedComponent;
    private ExplorerTaskbarProbeWindow? topLevelControlWindow;
    private bool applyingVisibility;
    private readonly TaskbarDockWindowAdapter taskbarDock;
    private readonly Win32TaskbarDockEnvironment taskbarEnvironment;
    private readonly DispatcherTimer dockTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private int settingsRefreshTicks;
    private bool stoppingDock;
    private bool applyingDockSettings;
    private string displayListKey = string.Empty;

    internal MainWindow(
        HostDisplayLoadResult displayLoad,
        HostDisplayActionController displayActions,
        TaskbarDockWindowAdapter taskbarDock,
        Win32TaskbarDockEnvironment taskbarEnvironment)
    {
        ArgumentNullException.ThrowIfNull(displayLoad);
        this.displayActions = displayActions ?? throw new ArgumentNullException(nameof(displayActions));
        this.taskbarDock = taskbarDock;
        this.taskbarEnvironment = taskbarEnvironment;
        InitializeComponent();
        AppWindow.Closing += MainWindow_Closing;
        this.displayActions.ProbeStateChanged += DisplayActions_ProbeStateChanged;

        AppWindow.Resize(new Windows.Graphics.SizeInt32(600, 720));
        taskbarDock.StateChanged += TaskbarDock_StateChanged;
        RefreshDockSettings();
        dockTimer.Tick += DockTimer_Tick;
        dockTimer.Start();

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

        materialCapabilities = ExplorerTaskbarProbeWindow.ProbeCapabilities();
        PopulateMaterialCombo();

        ShowHostErrors(displayLoad.Errors);
    }

    private void DockTimer_Tick(object? sender, object args)
    {
        var refresh = displayActions.RefreshPresentation();
        if (!refresh.IsSuccess) ShowHostError(refresh.Error!);
        if (++settingsRefreshTicks % 4 == 0) RefreshDockSettings();
    }

    private void RefreshDockPresentation()
    {
        if (stoppingDock) return;
        if (taskbarDock.IsOpen || taskbarDock.NeedsWindowRecovery)
            displayActions.RestoreCurrent();
    }

    private void TaskbarDock_StateChanged(object? sender, EventArgs args) => UpdateDockStatus();

    private void RefreshDockSettings()
    {
        applyingDockSettings = true;
        try
        {
            var displays = taskbarEnvironment.GetDisplays();
            var key = string.Join("|", displays.Select(item => $"{item.Id}:{item.IsPrimary}")) + ":" + taskbarDock.Preferences.TargetDisplayId;
            if (key != displayListKey || TargetDisplayCombo.Items.Count == 0)
            {
                displayListKey = key;
                TargetDisplayCombo.Items.Clear();
                TargetDisplayCombo.Items.Add(new ComboBoxItem { Content = "主显示器（自动）", Tag = null });
                foreach (var display in displays)
                    TargetDisplayCombo.Items.Add(new ComboBoxItem { Content = $"{display.Id}{(display.IsPrimary ? "（主显示器）" : string.Empty)}", Tag = display.Id });
                var preferred = taskbarDock.Preferences.TargetDisplayId;
                if (preferred is not null && !displays.Any(item => item.Id == preferred))
                    TargetDisplayCombo.Items.Add(new ComboBoxItem { Content = $"{preferred}（暂时不可用）", Tag = preferred });
                TargetDisplayCombo.SelectedItem = TargetDisplayCombo.Items.Cast<ComboBoxItem>().First(item => Equals(item.Tag, preferred));
            }
            RightGapInput.Value = taskbarDock.Preferences.RightGapDip;
            UpdateDockStatus();
        }
        catch (Exception exception)
        {
            DockStatusText.Text = $"显示器列表暂时不可用：{exception.GetType().Name}";
        }
        finally { applyingDockSettings = false; }
    }

    private void UpdateDockStatus()
    {
        var errors = new[] { taskbarDock.PreferenceError, taskbarDock.PresentationError, taskbarDock.VisualError }
            .Where(error => error is not null).Distinct().Select(error => FormatError(error!));
        DockStatusText.Text = string.Join(Environment.NewLine, new[] { taskbarDock.Status }.Concat(errors));
    }

    private void TargetDisplayCombo_SelectionChanged(object sender, SelectionChangedEventArgs args)
    {
        if (applyingDockSettings || TargetDisplayCombo.SelectedItem is not ComboBoxItem selected) return;
        taskbarDock.SetDisplay(selected.Tag as string);
        displayListKey = string.Empty;
        RefreshDockSettings();
    }

    private void RightGapInput_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (applyingDockSettings || DockStatusText is null || taskbarDock is null) return;
        if (double.IsFinite(args.NewValue) && args.NewValue == Math.Truncate(args.NewValue))
            taskbarDock.SetGap((int)args.NewValue);
        else ShowHostError(new("dock_gap_invalid", "右侧避让距离必须是 0 到 64 的整数。"));
        RefreshDockSettings();
    }

    private void SimulateFailureCheck_Changed(object sender, RoutedEventArgs args)
    {
        taskbarEnvironment.SimulateUnavailable = SimulateFailureCheck.IsChecked == true;
        taskbarDock.Refresh();
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

        var result = displayActions.SetVisibility(selectedComponent.Identity, VisibilityToggle.IsOn);
        if (result.Component is not null)
        {
            selectedComponent = result.Component;
            ApplyDisplay(selectedComponent);
        }

        applyingVisibility = true;
        VisibilityToggle.IsOn = selectedComponent?.IsVisible == true;
        applyingVisibility = false;
        ShowHostErrors(result.Errors);
        UpdateProbeButtons();
    }

    private void RunProbeButton_Click(object sender, RoutedEventArgs args)
    {
        if (selectedComponent is null)
        {
            return;
        }

        var outcome = displayActions.RunProbe(
            selectedComponent,
            new ExplorerTaskbarProbeRequest(SimulateFailureCheck.IsChecked == true, SelectedTransparencyMode));
        ShowProbeOutcome(outcome);
        UpdateProbeButtons();
    }

    private void StopProbeButton_Click(object sender, RoutedEventArgs args)
    {
        var result = displayActions.DetachProbe();
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
            var closeResult = CloseTopLevelControlWindow();
            if (!closeResult.IsSuccess)
            {
                ShowHostError(closeResult.Error!);
            }

            return;
        }

        var display = selectedComponent ?? HostComponentDisplayModel.From(new Component(
            new StableIdentity(new StableId("mtp")).CreateChild(new StableId("transparency-control")),
            CapabilityState.Available));
        var controlWindow = new ExplorerTaskbarProbeWindow();
        topLevelControlWindowOwner.TakeOwnership(new ExplorerTopLevelControlWindowResource(controlWindow));
        topLevelControlWindow = controlWindow;
        controlWindow.Closed += TopLevelControlWindow_Closed;
        try
        {
            controlWindow.InitializeView(display);
            controlWindow.PrepareHidden(new Windows.Graphics.SizeInt32(360, 60), useToolWindowPresenter: false);
            controlWindow.PrepareAsTopLevelControl();
            var position = AppWindow.Position;
            controlWindow.ShowAt(new Windows.Graphics.PointInt32(position.X + 24, position.Y + AppWindow.Size.Height - 90));
        }
        catch (Exception exception)
        {
            var errors = new List<StructuredError>
            {
                new("top_level_control_window_show_failed", "The diagnostic top-level window could not be shown.", exception.GetType().Name),
            };
            var cleanup = CloseTopLevelControlWindow();
            if (!cleanup.IsSuccess)
            {
                errors.Add(cleanup.Error!);
            }

            ShowHostErrors(errors);
            return;
        }

        TopLevelControlButton.Content = "关闭透明顶级对照窗口";

        // The material dropdown is the single source of truth for this window. The transparency mode
        // dropdown only feeds the embed probe request, so the two controls cannot fight over the surface.
        var frameDetail = Win32ExplorerTaskbarEmbedAdapter.PrepareControlWindowSurface(controlWindow.WindowHandle);
        controlStatusBase = $"顶级对照窗口已显示（未嵌入任务栏）。窗口表面：{frameDetail}；backdrop_state：{controlWindow.DescribeBackdropState()}。材质由「材质」下拉框控制，可实时切换对比。";
        controlWindow.AppWindow.Changed += TopLevelControlWindow_Changed;
        ApplyMaterialToControlWindow();
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
        4 => ProbeTransparencyMode.SolidPaint,
        _ => ProbeTransparencyMode.Backdrop,
    };

    /// <summary>Material dropdown entries, filled from the core selection order so the UI cannot drift from it.</summary>
    private void PopulateMaterialCombo()
    {
        MaterialCombo.Items.Clear();
        foreach (var kind in MaterialResolver.SelectionOrder)
        {
            // The translucency hint is part of the label because Mica is opaque by design; without it
            // the maintainer reasonably expects the opacity slider to make the window see-through.
            MaterialCombo.Items.Add($"{MaterialResolver.Describe(kind)}（{(MaterialResolver.IsTranslucent(kind) ? "可透" : "不透")}）");
        }

        MaterialCombo.SelectedIndex = MaterialResolver.SelectionOrder.ToList().IndexOf(MaterialKind.Acrylic);
        UpdateMaterialStatus();
    }

    private MaterialSpec SelectedMaterialSpec => new(
        MaterialCombo.SelectedIndex >= 0 && MaterialCombo.SelectedIndex < MaterialResolver.SelectionOrder.Count
            ? MaterialResolver.SelectionOrder[MaterialCombo.SelectedIndex]
            : MaterialKind.Acrylic,
        MaterialOpacitySlider.Value);

    /// <summary>
    /// Shows the requested material, the material that is actually available on this build and the
    /// downgrade reason, so a fallback is never silent.
    /// </summary>
    private void UpdateMaterialStatus()
    {
        if (MaterialCombo.SelectedIndex < 0)
        {
            MaterialStatusText.Text = string.Empty;
            return;
        }

        var resolution = MaterialResolver.Resolve(SelectedMaterialSpec, materialCapabilities);
        var translucency = MaterialResolver.IsTranslucent(resolution.Effective.Kind)
            ? "可透出后面内容"
            : "不透出后面内容";
        MaterialStatusText.Text = resolution.WasDowngraded
            ? $"当前材质：{MaterialResolver.Describe(resolution.Effective.Kind)}（不透明度 {resolution.Effective.Opacity:F2}）｜{translucency}｜{MaterialResolver.DescribeOpacityMeaning(resolution.Effective.Kind)}｜{resolution.DowngradeReason}"
            : $"当前材质：{MaterialResolver.Describe(resolution.Effective.Kind)}（不透明度 {resolution.Effective.Opacity:F2}）｜{translucency}｜{MaterialResolver.DescribeOpacityMeaning(resolution.Effective.Kind)}";
    }

    private MaterialCapabilities materialCapabilities = MaterialCapabilities.SolidOnly;

    private void MaterialCombo_SelectionChanged(object sender, SelectionChangedEventArgs args)
    {
        UpdateMaterialStatus();
        ApplyMaterialToControlWindow();
    }

    private void MaterialOpacitySlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs args)
    {
        if (MaterialStatusText is not null)
        {
            UpdateMaterialStatus();
            ApplyMaterialToControlWindow();
        }
    }

    /// <summary>
    /// Applies the selected material to the open top-level control window so the maintainer can compare
    /// materials on one window without reopening it. Resolution and downgrade happen here, in Host code;
    /// the window only renders the effective value it is handed. Solid additionally needs one GDI paint
    /// on the window DC, which is performed through the adapter boundary.
    /// </summary>
    private void ApplyMaterialToControlWindow()
    {
        if (topLevelControlWindow is null || MaterialCombo.SelectedIndex < 0)
        {
            return;
        }

        var resolution = MaterialResolver.Resolve(SelectedMaterialSpec, materialCapabilities);
        var applied = topLevelControlWindow.TryApplyMaterial(resolution.Effective, out var detail);
        var downgrade = resolution.WasDowngraded ? $"{resolution.DowngradeReason} " : string.Empty;
        var meaning = MaterialResolver.DescribeOpacityMeaning(resolution.Effective.Kind);
        ProbeStatusText.Text = $"{controlStatusBase}\n材质：{MaterialResolver.Describe(resolution.Effective.Kind)}（不透明度 {resolution.Effective.Opacity:F2}）｜{meaning}｜{downgrade}{(applied ? detail : $"应用失败：{detail}")}";
        ProbeStatusText.Visibility = Visibility.Visible;

        // Solid mode owes one GDI paint on the window DC. Defer it to a low-priority queue item so the
        // XAML framework has already connected the brush; painting before that still works on this machine
        // but the ordering matches WinUIEx, which paints inside OnTargetConnected.
        if (applied && topLevelControlWindow.RequiresSurfacePaint)
        {
            var target = topLevelControlWindow;
            var baseText = ProbeStatusText.Text;
            var requested = SelectedMaterialSpec;
            var queued = target.DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
            {
                if (!ReferenceEquals(topLevelControlWindow, target) || SelectedMaterialSpec != requested ||
                    !target.RequiresSurfacePaint || !target.IsBackdropConnected) return;
                var painted = Win32ExplorerTaskbarEmbedAdapter.PaintWindowSurfaceOnce(target.WindowHandle, out var paintResult);
                if (painted) target.MarkSurfacePainted();
                ProbeStatusText.Text = $"{baseText}\n透明表面绘制：{(painted ? paintResult : $"失败：{paintResult}")}";
                ProbeStatusText.Visibility = Visibility.Visible;
            });
            if (!queued) ShowHostError(new("control_surface_queue_failed", "对照窗口透明表面绘制未能进入队列。"));
        }
    }

    private void TopLevelControlWindow_Closed(object sender, WindowEventArgs args)
    {
        if (sender is not ExplorerTaskbarProbeWindow closed || !ReferenceEquals(topLevelControlWindow, closed))
        {
            return;
        }

        closed.Closed -= TopLevelControlWindow_Closed;
        try
        {
            closed.AppWindow.Changed -= TopLevelControlWindow_Changed;
        }
        catch (Exception)
        {
            // The AppWindow may already be gone after Closed.
        }

        topLevelControlWindow = null;
        TopLevelControlButton.Content = "显示透明顶级对照窗口";
    }

    private CoreResult<bool> CloseTopLevelControlWindow()
    {
        return topLevelControlWindowOwner.Close();
    }

    private void DisplayActions_ProbeStateChanged(object? sender, EventArgs args)
    {
        var state = displayActions.ProbeState;
        if (!state.IsEmbedded && state.Error is not null)
        {
            var builder = new StringBuilder();
            builder.AppendLine(ExplorerTaskbarProbeOutcome.FallbackMessage);
            builder.AppendLine(FormatError(state.Error));
            if (state.FallbackError is not null)
            {
                builder.AppendLine($"独立贴靠窗口恢复失败：{FormatError(state.FallbackError)}");
            }

            ProbeStatusText.Text = builder.ToString().TrimEnd();
            ProbeStatusText.Visibility = Visibility.Visible;
        }

        UpdateProbeButtons();
    }

    private void ShowProbeOutcome(ExplorerTaskbarProbeOutcome outcome)
    {
        if (outcome.Report is not null)
        {
            ProbeStatusText.Text = ExplorerTaskbarProbeReportFormatter.Format(outcome.Report);
            if (outcome.Error is not null)
            {
                ProbeStatusText.Text += $"{Environment.NewLine}独立贴靠窗口关闭失败：{FormatError(outcome.Error)}";
            }
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
        var probeState = displayActions.ProbeState;
        RunProbeButton.IsEnabled = canProbe;
        StopProbeButton.IsEnabled = probeState.IsEmbedded || probeState.IsRecoveryPending;
        StopProbeButton.Content = probeState.IsRecoveryPending
            ? "重试恢复独立贴靠"
            : "停止探针，恢复独立贴靠";
    }

    public void ShowHostError(StructuredError error)
    {
        ArgumentNullException.ThrowIfNull(error);
        ErrorText.Text = FormatError(error);
        ErrorText.Visibility = Visibility.Visible;
    }

    private void ShowHostErrors(IReadOnlyList<StructuredError> errors)
    {
        ErrorText.Text = string.Join(Environment.NewLine, errors.Select(FormatError));
        ErrorText.Visibility = errors.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private void MainWindow_Closing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        var errors = new List<StructuredError>();
        var controlWindowClose = CloseTopLevelControlWindow();
        if (!controlWindowClose.IsSuccess)
        {
            args.Cancel = true;
            ShowHostError(controlWindowClose.Error!);
            return;
        }

        var shutdown = displayActions.Shutdown();
        errors.AddRange(shutdown.Errors);
        if (errors.Count > 0)
        {
            args.Cancel = true;
            ShowHostErrors(errors);
            return;
        }

        stoppingDock = true;
        AppWindow.Closing -= MainWindow_Closing;
        dockTimer.Stop();
        dockTimer.Tick -= DockTimer_Tick;
        taskbarDock.StateChanged -= TaskbarDock_StateChanged;
        displayActions.ProbeStateChanged -= DisplayActions_ProbeStateChanged;
        displayActions.Dispose();
    }

    private static string FormatError(StructuredError error) =>
        string.IsNullOrWhiteSpace(error.Path)
            ? $"{error.Code}: {error.Message}"
            : $"{error.Code}: {error.Message} ({error.Path})";
}
