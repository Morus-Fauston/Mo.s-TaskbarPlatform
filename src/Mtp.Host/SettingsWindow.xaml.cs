using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Mtp.Platform.Core;
using Mtp.Contracts;
using Windows.UI.ViewManagement;

namespace Mtp.Host;

/// <summary>Production settings projection; business state and persistence remain in their owning controllers.</summary>
public sealed partial class SettingsWindow : Window
{
    private readonly HostSettingsController settings;
    private readonly HostConsoleController host;
    private readonly DisplaySelectionBinding displaySelection;
    private readonly CancellationTokenSource lifetime = new();
    private readonly Dictionary<StableIdentity, ComponentRow> components = [];
    private readonly Dictionary<StableIdentity, HintRow> hints = [];
    private readonly Dictionary<string, ApplicationRow> applications = new(StringComparer.Ordinal);
    private readonly HashSet<string> retrying = new(StringComparer.Ordinal);
    private bool rendering;
    private bool disposed;
    private bool queued;
    private string? operationError;
    private readonly AccessibilitySettings accessibility = new();
    private HostAppearancePreferences? appliedAppearance;
    private ElementTheme appliedTheme;
    private bool appliedHighContrast;
    private bool highContrastSubscribed;
    private bool lastHighContrast;
    private string? highContrastNotice;
    private long materialGeneration;
    private string windowMaterialStatus = "设置窗口材质尚未应用";

    internal SettingsWindow(HostSettingsController settings, HostConsoleController host, Func<IReadOnlyList<TaskbarDockDisplay>> displays)
    {
        this.settings = settings;
        this.host = host;
        rendering = true;
        InitializeComponent();
        foreach (var material in MaterialResolver.SelectionOrder)
            MaterialCombo.Items.Add(new ComboBoxItem { Content = MaterialResolver.Describe(material), Tag = material.ToString() });
        displaySelection = new(TargetDisplayCombo, RightGapInput, host, displays, DispatcherQueue);
        VersionText.Text = "程序集版本 " + (typeof(SettingsWindow).Assembly.GetName().Version?.ToString() ?? "未知");
        AppWindow.Resize(new(960, 720));
        AppWindow.Changed += ConstrainSize;
        AppWindow.Closing += Closing;
        Closed += WindowClosed;
        host.Changed += RequestRender;
        Root.ActualThemeChanged += ActualThemeChanged;
        try
        {
            accessibility.HighContrastChanged += HighContrastChanged;
            highContrastSubscribed = true;
        }
        catch (System.Runtime.InteropServices.COMException)
        { highContrastNotice = "高对比变化通知不可用，随现有 Host 刷新读取状态。"; }
        rendering = false;
        Render();
    }

    private void RequestRender()
    {
        if (disposed || queued) return;
        queued = true;
        if (!DispatcherQueue.TryEnqueue(() => { queued = false; if (!disposed) Render(); })) queued = false;
    }

    private void Render(bool resetInputs = false)
    {
        if (disposed || rendering) return;
        rendering = true;
        try
        {
            var snapshot = settings.GetSnapshot();
            Root.RequestedTheme = snapshot.Preferences.Appearance.Theme switch
            {
                HostTheme.Light => ElementTheme.Light,
                HostTheme.Dark => ElementTheme.Dark,
                _ => ElementTheme.Default,
            };
            ApplyWindowAppearance(snapshot.Preferences.Appearance);
            Navigation.IsBackEnabled = snapshot.Navigation.CanGoBack;
            var selected = snapshot.Navigation.Current;
            Navigation.SelectedItem = selected switch
            {
                SettingsPage.Layout => LayoutNav,
                SettingsPage.Flyouts => FlyoutsNav,
                SettingsPage.Official => OfficialNav,
                SettingsPage.About => AboutNav,
                SettingsPage.Global => GlobalNav,
                _ => ApplicationsNav,
            };
            PageTitle.Text = ((NavigationViewItem)Navigation.SelectedItem).Content.ToString();
            ApplicationsPanel.Visibility = Show(selected == SettingsPage.Applications);
            LayoutPanel.Visibility = Show(selected == SettingsPage.Layout);
            FlyoutsPanel.Visibility = Show(selected == SettingsPage.Flyouts);
            OfficialPanel.Visibility = Show(selected == SettingsPage.Official);
            AboutPanel.Visibility = Show(selected == SettingsPage.About);
            GlobalPanel.Visibility = Show(selected == SettingsPage.Global);
            RenderApplications(snapshot);
            RenderComponents(snapshot);
            RenderHints(snapshot);
            if (RightGapInput.FocusState == FocusState.Unfocused) displaySelection.Refresh();
            DisplayStatus.Text = displaySelection.Error ?? host.TargetSummary;
            ConnectionStatus.Text = host.CommunicationStatus;
            RetryBrokerButton.Visibility = Show(host.GetRecovery(null)?.CanRetry == true || retrying.Contains(""));
            RetryBrokerButton.IsEnabled = !retrying.Contains("");
            RetryBrokerButton.Content = retrying.Contains("") ? "正在恢复平台连接…" : "重试平台连接";
            var appearance = snapshot.Preferences.Appearance;
            var hintPreferences = snapshot.Preferences.Hints ?? new();
            if (resetInputs || !HintPositionCombo.IsDropDownOpen) SelectTag(HintPositionCombo, hintPreferences.DefaultPosition.ToString());
            AllowHintPositionToggle.IsOn = hintPreferences.AllowApplicationPosition;
            if (!ThemeCombo.IsDropDownOpen) SelectTag(ThemeCombo, appearance.Theme.ToString());
            if (!MaterialCombo.IsDropDownOpen) SelectTag(MaterialCombo, appearance.Material.ToString());
            if (resetInputs || OpacityInput.FocusState == FocusState.Unfocused) OpacityInput.Value = appearance.Opacity;
            OpacityInput.IsEnabled = appearance.Material != MaterialKind.None;
            OpacityMeaning.Text = MaterialResolver.DescribeOpacityMeaning(appearance.Material);
            MaterialStatusText.Text = snapshot.MaterialStatus + "\n" + windowMaterialStatus +
                (highContrastNotice is null ? "" : "\n" + highContrastNotice);
            string? error = operationError ?? snapshot.Error?.Message ?? displaySelection.Error ?? host.PreferenceError?.Message;
            error ??= host.CurrentError;
            ErrorBar.Message = error ?? "";
            ErrorBar.IsOpen = error is not null;
        }
        catch (Exception error) when (error is IOException or InvalidOperationException or ArgumentException or ObjectDisposedException)
        {
            ErrorBar.Message = "无法刷新设置，已保留当前显示：" + error.Message;
            ErrorBar.IsOpen = true;
        }
        finally { rendering = false; }
    }

    private void ApplyWindowAppearance(HostAppearancePreferences appearance)
    {
        bool highContrast;
        try
        {
            highContrast = lastHighContrast = accessibility.HighContrast;
            highContrastNotice = highContrastSubscribed ? null : "高对比变化通知不可用，随现有 Host 刷新读取状态。";
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            highContrast = lastHighContrast;
            highContrastNotice = "高对比状态暂时不可读，保留最后确认状态并随 Host 刷新重试。";
        }
        if (appliedAppearance == appearance && appliedTheme == Root.ActualTheme && appliedHighContrast == highContrast) return;
        appliedAppearance = appearance;
        appliedTheme = Root.ActualTheme;
        appliedHighContrast = highContrast;
        long current = ++materialGeneration;
        var resolved = MaterialResolver.Resolve(new(appearance.Material, appearance.Opacity), new(true,
            Microsoft.UI.Composition.SystemBackdrops.DesktopAcrylicController.IsSupported(),
            Microsoft.UI.Composition.SystemBackdrops.MicaController.IsSupported()));
        MaterialKind effective = highContrast ? MaterialKind.None : resolved.Effective.Kind;
        string? reason = highContrast ? "高对比模式使用不透明系统背景" : resolved.DowngradeReason;
        SystemBackdrop = null;
        try
        {
            if (effective is MaterialKind.Acrylic or MaterialKind.Mica)
            {
                Root.Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
                SystemBackdrop = new Islands.ConfiguredMaterialBackdrop(effective, resolved.Effective.Opacity, (_, value) =>
                {
                    if (disposed || materialGeneration != current) return;
                    string? state = value?.GetType().GetProperty("state")?.GetValue(value)?.ToString();
                    windowMaterialStatus = "设置窗口：" + MaterialResolver.Describe(effective) + " · API 状态 " + (state ?? "已设置") +
                        (reason is null ? "" : " · " + reason);
                    RequestRender();
                });
            }
            else ApplySolidBackground(effective == MaterialKind.Solid ? resolved.Effective.Opacity : 1, highContrast);
            windowMaterialStatus = "设置窗口：请求" + MaterialResolver.Describe(appearance.Material) + "，已应用" + MaterialResolver.Describe(effective) +
                (reason is null ? "" : " · " + reason) + "；外观待实际确认";
        }
        catch (Exception error) when (error is System.Runtime.InteropServices.COMException or InvalidOperationException or ArgumentException)
        {
            SystemBackdrop = null;
            ApplySolidBackground(1, highContrast);
            windowMaterialStatus = "设置窗口：材质不可用，已回退不透明背景，保留原请求 · " + error.Message;
        }
    }

    private void ApplySolidBackground(double opacity, bool highContrast)
    {
        var color = highContrast ? new UISettings().GetColorValue(UIColorType.Background) :
            SurfacePalette.Background is SolidColorBrush brush ? brush.Color :
            Root.ActualTheme == ElementTheme.Dark ? Windows.UI.Color.FromArgb(255, 32, 32, 32) : Windows.UI.Color.FromArgb(255, 243, 243, 243);
        color.A = (byte)Math.Round(255 * opacity);
        Root.Background = new SolidColorBrush(color);
    }

    private void ActualThemeChanged(FrameworkElement sender, object args) => RequestRender();
    private void HighContrastChanged(AccessibilitySettings sender, object args) => RequestRender();

    private void RenderApplications(HostSettingsSnapshot snapshot)
    {
        var active = snapshot.Applications.Select(app => app.ApplicationId).ToHashSet(StringComparer.Ordinal);
        foreach (var id in applications.Keys.Where(id => !active.Contains(id)).ToArray())
        {
            applications[id].Detach();
            ApplicationRows.Children.Remove(applications[id].Panel);
            applications.Remove(id);
        }
        for (int index = 0; index < snapshot.Applications.Count; index++)
        {
            var app = snapshot.Applications[index];
            if (!applications.TryGetValue(app.ApplicationId, out var row))
            {
                row = CreateApplicationRow(app.ApplicationId);
                applications.Add(app.ApplicationId, row);
            }
            KeepPosition(ApplicationRows, row.Panel, index);
            row.Title.Text = app.ApplicationId;
            string state = app.IsInteractive ? "已连接，可交互" : app.IsConnected ? "连接中，正在确认声明与状态" : "连接不可用";
            row.Status.Text = state + (app.LastError is { } error ? " · " + error.Message + "（" + error.Code + "）" : "");
            row.Groups.Text = app.Declaration is null ? "正在等待有效功能组声明。" : "功能组：" + string.Join("、", app.Declaration.FeatureGroups.Select(group => group.Identity.LocalId.Value));
            var recovery = host.GetRecovery(app.ApplicationId);
            row.Recovery.Text = recovery is null ? "" : $"恢复尝试 {recovery.Attempts} 次，进程重启 {recovery.Restarts} 次" + (recovery.Error is null ? "" : " · " + recovery.Error);
            row.Retry.Visibility = Show(recovery?.CanRetry == true || retrying.Contains(app.ApplicationId));
            row.Retry.IsEnabled = !retrying.Contains(app.ApplicationId);
            row.Retry.Content = retrying.Contains(app.ApplicationId) ? "正在恢复…" : "重试连接";
        }
        ApplicationsEmpty.Visibility = Show(snapshot.Applications.Count == 0);
    }

    private void RenderComponents(HostSettingsSnapshot snapshot)
    {
        var active = snapshot.Components.Select(component => component.Identity).ToHashSet();
        foreach (var identity in components.Keys.Where(identity => !active.Contains(identity)).ToArray())
        {
            components[identity].Detach();
            ComponentRows.Children.Remove(components[identity].Panel);
            components.Remove(identity);
        }
        for (int index = 0; index < snapshot.Components.Count; index++)
        {
            var component = snapshot.Components[index];
            if (!components.TryGetValue(component.Identity, out var row))
            {
                row = CreateComponentRow(component.Identity);
                components.Add(component.Identity, row);
            }
            KeepPosition(ComponentRows, row.Panel, index);
            row.Title.Text = component.Identity.LocalId.Value;
            row.Details.Text = string.Join(" / ", component.Identity.Segments.Select(segment => segment.Value)) + " · " + component.StatusLabel;
            row.Visible.IsOn = component.IsVisible;
            row.Up.IsEnabled = index > 0;
            row.Down.IsEnabled = index < snapshot.Components.Count - 1;
            var organization = snapshot.Groupings.FirstOrDefault(value => value.Identity == component.Identity);
            row.Grouping.Visibility = Show(organization is not null);
            if (organization is not null)
            {
                row.Grouping.IsEnabled = organization.Capability == DynamicGrouping.UserChoice;
                var choices = organization.Capability == DynamicGrouping.UserChoice
                    ? new[] { DynamicGrouping.Together, DynamicGrouping.Separate } : new[] { organization.Capability };
                if (!row.Grouping.Items.Cast<ComboBoxItem>().Select(value => (DynamicGrouping)value.Tag).SequenceEqual(choices))
                {
                    row.Grouping.Items.Clear();
                    foreach (var choice in choices) row.Grouping.Items.Add(new ComboBoxItem
                    { Content = choice == DynamicGrouping.Together ? "合并显示" : "分别显示", Tag = choice });
                }
                row.Grouping.SelectedItem = row.Grouping.Items.Cast<ComboBoxItem>().Single(value => (DynamicGrouping)value.Tag == organization.Effective);
            }
        }
        ComponentsEmpty.Visibility = Show(snapshot.Components.Count == 0);
    }

    private ApplicationRow CreateApplicationRow(string id)
    {
        var panel = new StackPanel { Spacing = 8 };
        var title = new TextBlock { Style = (Style)Application.Current.Resources["SubtitleTextBlockStyle"] };
        var status = new TextBlock { TextWrapping = TextWrapping.Wrap };
        var groups = new TextBlock { TextWrapping = TextWrapping.Wrap };
        var recovery = new TextBlock { TextWrapping = TextWrapping.Wrap };
        var retry = new Button { Content = "重试连接" };
        AutomationProperties.SetAutomationId(panel, "mtp-settings-app-" + Uri.EscapeDataString(id));
        AutomationProperties.SetAutomationId(status, "mtp-settings-app-state-" + Uri.EscapeDataString(id));
        AutomationProperties.SetAutomationId(retry, "mtp-settings-app-retry-" + Uri.EscapeDataString(id));
        AutomationProperties.SetName(retry, "重试 " + id + " 的连接");
        RoutedEventHandler handler = (_, _) => RetryAsync(id);
        retry.Click += handler;
        panel.Children.Add(title); panel.Children.Add(status); panel.Children.Add(groups); panel.Children.Add(recovery); panel.Children.Add(retry);
        return new(panel, title, status, groups, recovery, retry, () => retry.Click -= handler);
    }

    private void RenderHints(HostSettingsSnapshot snapshot)
    {
        var active = snapshot.HintEntries.Select(entry => entry.Identity).ToHashSet();
        foreach (var identity in hints.Keys.Where(identity => !active.Contains(identity)).ToArray())
        {
            hints[identity].Detach();
            HintRows.Children.Remove(hints[identity].Panel);
            hints.Remove(identity);
        }
        for (int index = 0; index < snapshot.HintEntries.Count; index++)
        {
            var entry = snapshot.HintEntries[index];
            if (!hints.TryGetValue(entry.Identity, out var row))
            {
                row = CreateHintRow(entry.Identity);
                hints.Add(entry.Identity, row);
            }
            KeepPosition(HintRows, row.Panel, index);
            row.Title.Text = entry.Identity.LocalId.Value;
            row.Details.Text = string.Join(" / ", entry.Identity.Segments.Select(segment => segment.Value)) + " · " +
                (entry.Kind == FlyoutKind.InteractiveHint ? "可交互短提示" : "普通短提示") +
                (entry.IsAvailable ? "" : " · 连接不可用，恢复后按此偏好显示");
            row.Visible.IsOn = entry.IsVisible;
        }
        HintsEmpty.Visibility = Show(snapshot.HintEntries.Count == 0);
    }

    private HintRow CreateHintRow(StableIdentity identity)
    {
        string key = string.Join("/", identity.Segments.Select(segment => Uri.EscapeDataString(segment.Value)));
        var panel = new Grid { ColumnSpacing = 12, Padding = new Thickness(0, 8, 0, 8) };
        panel.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
        panel.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        var labels = new StackPanel { Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
        var title = new TextBlock { TextWrapping = TextWrapping.Wrap };
        var details = new TextBlock { TextWrapping = TextWrapping.Wrap, Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"] };
        labels.Children.Add(title); labels.Children.Add(details);
        var visible = new ToggleSwitch { MinWidth = 0, OnContent = "", OffContent = "", VerticalAlignment = VerticalAlignment.Center };
        AutomationProperties.SetAutomationId(panel, "mtp-settings-hint-" + key);
        AutomationProperties.SetAutomationId(visible, "mtp-settings-hint-visible-" + key);
        AutomationProperties.SetName(visible, "显示短提示 " + key);
        RoutedEventHandler toggled = (_, _) => { if (!rendering && !disposed) Apply(() => settings.SetHintVisibility(identity, visible.IsOn)); };
        visible.Toggled += toggled;
        Grid.SetColumn(visible, 1);
        panel.Children.Add(labels); panel.Children.Add(visible);
        return new(panel, title, details, visible, () => visible.Toggled -= toggled);
    }

    private ComponentRow CreateComponentRow(StableIdentity identity)
    {
        string key = string.Join("/", identity.Segments.Select(segment => Uri.EscapeDataString(segment.Value)));
        var panel = new Grid { ColumnSpacing = 12, Padding = new Thickness(0, 8, 0, 8) };
        panel.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
        panel.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        panel.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        panel.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        var labels = new StackPanel { Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
        var title = new TextBlock { TextWrapping = TextWrapping.Wrap };
        var details = new TextBlock { TextWrapping = TextWrapping.Wrap, Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"] };
        labels.Children.Add(title); labels.Children.Add(details);
        var visible = new ToggleSwitch { MinWidth = 0, OnContent = "", OffContent = "", VerticalAlignment = VerticalAlignment.Center };
        var up = new Button { Content = new FontIcon { Glyph = "\uE70E" }, VerticalAlignment = VerticalAlignment.Center };
        var down = new Button { Content = new FontIcon { Glyph = "\uE70D" }, VerticalAlignment = VerticalAlignment.Center };
        var grouping = new ComboBox { MinWidth = 110, VerticalAlignment = VerticalAlignment.Center, Visibility = Visibility.Collapsed };
        panel.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        AutomationProperties.SetAutomationId(grouping, "mtp-settings-grouping-" + key);
        AutomationProperties.SetName(grouping, "组织 " + key);
        AutomationProperties.SetAutomationId(panel, "mtp-settings-component-" + key);
        AutomationProperties.SetAutomationId(visible, "mtp-settings-visible-" + key);
        AutomationProperties.SetAutomationId(up, "mtp-settings-up-" + key);
        AutomationProperties.SetAutomationId(down, "mtp-settings-down-" + key);
        AutomationProperties.SetName(visible, "显示 " + key);
        AutomationProperties.SetName(up, "上移 " + key);
        AutomationProperties.SetName(down, "下移 " + key);
        ToolTipService.SetToolTip(up, "上移"); ToolTipService.SetToolTip(down, "下移");
        RoutedEventHandler toggled = (_, _) => { if (!rendering && !disposed) Apply(() => settings.SetVisibility(identity, visible.IsOn)); };
        RoutedEventHandler upward = (_, _) => Apply(() => settings.MoveComponent(identity, -1));
        RoutedEventHandler downward = (_, _) => Apply(() => settings.MoveComponent(identity, 1));
        SelectionChangedEventHandler grouped = (_, _) =>
        {
            if (!rendering && !disposed && grouping.SelectedItem is ComboBoxItem { Tag: DynamicGrouping value })
                Apply(() => settings.SetGrouping(identity, value));
        };
        grouping.SelectionChanged += grouped;
        visible.Toggled += toggled; up.Click += upward; down.Click += downward;
        Grid.SetColumn(visible, 1); Grid.SetColumn(up, 2); Grid.SetColumn(down, 3);
        Grid.SetColumn(grouping, 4);
        panel.Children.Add(labels); panel.Children.Add(visible); panel.Children.Add(up); panel.Children.Add(down);
        panel.Children.Add(grouping);
        return new(panel, title, details, visible, up, down, grouping, () =>
        { visible.Toggled -= toggled; up.Click -= upward; down.Click -= downward; grouping.SelectionChanged -= grouped; });
    }

    private static void KeepPosition(Panel owner, UIElement child, int index)
    {
        int previous = owner.Children.IndexOf(child);
        if (previous == index) return;
        if (previous >= 0) owner.Children.RemoveAt(previous);
        owner.Children.Insert(index, child);
    }

    private static Visibility Show(bool show) => show ? Visibility.Visible : Visibility.Collapsed;
    private static void SelectTag(ComboBox combo, string tag) => combo.SelectedItem = combo.Items.Cast<ComboBoxItem>().First(item => Equals(item.Tag, tag));

    private void NavigationChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (rendering || disposed || args.SelectedItem is not NavigationViewItem item || !Enum.TryParse<SettingsPage>(item.Tag?.ToString(), out var page)) return;
        operationError = null;
        settings.Navigate(page);
        Render();
    }
    private void BackRequested(NavigationView sender, NavigationViewBackRequestedEventArgs args)
    {
        if (disposed) return;
        operationError = null;
        settings.Back();
        Render();
    }
    private void DisplayChanged(object sender, SelectionChangedEventArgs args) { if (!rendering && !disposed) displaySelection.SelectionChanged(); }
    private void GapChanged(NumberBox sender, NumberBoxValueChangedEventArgs args) { if (!rendering && !disposed) displaySelection.GapChanged(args.NewValue); }
    private void AppearanceChanged(object sender, SelectionChangedEventArgs args) => SaveAppearance();
    private void HintPositionChanged(object sender, SelectionChangedEventArgs args) => SaveHints();
    private void HintOverrideChanged(object sender, RoutedEventArgs args) => SaveHints();
    private void SaveHints()
    {
        if (rendering || disposed || HintPositionCombo.SelectedItem is not ComboBoxItem selected ||
            !Enum.TryParse<FlyoutPosition>(selected.Tag?.ToString(), out var position)) return;
        Apply(() => settings.SetHints(new(position, AllowHintPositionToggle.IsOn)));
    }
    private void OpacityChanged(NumberBox sender, NumberBoxValueChangedEventArgs args) => SaveAppearance();
    private void SaveAppearance()
    {
        if (rendering || disposed || ThemeCombo.SelectedItem is not ComboBoxItem theme || MaterialCombo.SelectedItem is not ComboBoxItem material) return;
        if (!Enum.TryParse<HostTheme>(theme.Tag?.ToString(), out var selectedTheme) || !Enum.TryParse<MaterialKind>(material.Tag?.ToString(), out var selectedMaterial)) return;
        Apply(() => settings.SetAppearance(new(selectedTheme, selectedMaterial, OpacityInput.Value)));
    }
    private void Apply(Func<CoreResult<HostSettingsSnapshot>> action)
    {
        if (disposed) return;
        var result = action();
        operationError = result.IsSuccess ? null : result.Error?.Message;
        Render(resetInputs: !result.IsSuccess);
    }

    private void RetryBrokerClicked(object sender, RoutedEventArgs args) => RetryAsync(null);
    private async void RetryAsync(string? applicationId)
    {
        string key = applicationId ?? "";
        if (disposed || !retrying.Add(key)) return;
        operationError = null;
        Render();
        try
        {
            var result = await settings.RetryAsync(applicationId, lifetime.Token);
            if (!disposed && !result.Accepted) operationError = result.Message;
        }
        catch (OperationCanceledException) { }
        catch (Exception error) when (error is IOException or InvalidOperationException or ObjectDisposedException)
        { if (!disposed) operationError = error.Message; }
        finally { retrying.Remove(key); if (!disposed) Render(); }
    }

    private void ConstrainSize(Microsoft.UI.Windowing.AppWindow sender, Microsoft.UI.Windowing.AppWindowChangedEventArgs args)
    {
        if (!args.DidSizeChange) return;
        double scale = Islands.NativeWindows.GetDpiForWindow(WinRT.Interop.WindowNative.GetWindowHandle(this)) / 96d;
        int width = Math.Max(sender.Size.Width, (int)Math.Ceiling(600 * scale));
        int height = Math.Max(sender.Size.Height, (int)Math.Ceiling(480 * scale));
        if (width != sender.Size.Width || height != sender.Size.Height) sender.Resize(new(width, height));
    }
    private void Closing(Microsoft.UI.Windowing.AppWindow sender, Microsoft.UI.Windowing.AppWindowClosingEventArgs args)
    {
        if (!host.Shutdown()) { args.Cancel = true; Render(); return; }
        Release();
    }
    private void WindowClosed(object sender, WindowEventArgs args)
    {
        host.Shutdown();
        Release();
    }
    private void Release()
    {
        if (disposed) return;
        disposed = true;
        lifetime.Cancel();
        host.Changed -= RequestRender;
        Root.ActualThemeChanged -= ActualThemeChanged;
        if (highContrastSubscribed)
        {
            highContrastSubscribed = false;
            try { accessibility.HighContrastChanged -= HighContrastChanged; }
            catch (System.Runtime.InteropServices.COMException) { }
        }
        materialGeneration++;
        SystemBackdrop = null;
        displaySelection.Stop();
        settings.Dispose();
        AppWindow.Changed -= ConstrainSize;
        AppWindow.Closing -= Closing;
        Closed -= WindowClosed;
        foreach (var row in components.Values) row.Detach();
        foreach (var row in hints.Values) row.Detach();
        foreach (var row in applications.Values) row.Detach();
        components.Clear(); hints.Clear(); applications.Clear(); retrying.Clear();
        lifetime.Dispose();
    }

    private sealed record ComponentRow(Grid Panel, TextBlock Title, TextBlock Details, ToggleSwitch Visible, Button Up, Button Down, ComboBox Grouping, Action Detach);
    private sealed record HintRow(Grid Panel, TextBlock Title, TextBlock Details, ToggleSwitch Visible, Action Detach);
    private sealed record ApplicationRow(StackPanel Panel, TextBlock Title, TextBlock Status, TextBlock Groups, TextBlock Recovery, Button Retry, Action Detach);
}
