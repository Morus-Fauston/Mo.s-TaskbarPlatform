using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Mtp.Host;

/// <summary>Navigation, rendering and event forwarding only.</summary>
public sealed partial class MainWindow : Window
{
    private readonly HostConsoleController controller;
    private readonly DisplaySelectionBinding displaySelection;
    private bool rendering;
    internal MainWindow(HostConsoleController controller, Func<IReadOnlyList<TaskbarDockDisplay>> displays)
    {
        this.controller = controller;
        InitializeComponent();
        displaySelection = new(TargetDisplayCombo, RightGapInput, controller, displays, DispatcherQueue);
        foreach (var dimension in HostTestRun.Dimensions) DimensionCombo.Items.Add(dimension);
        DimensionCombo.SelectedIndex = 0;
        AppWindow.Changed += ConstrainSize;
        AppWindow.Resize(new Windows.Graphics.SizeInt32(1000, 960));
        controller.Changed += Render;
        Closed += (_, _) =>
        {
            AppWindow.Changed -= ConstrainSize;
            controller.Shutdown();
            controller.Changed -= Render;
            displaySelection.Stop();
        };
        AppWindow.Closing += (_, args) =>
        {
            if (!controller.Shutdown()) { args.Cancel = true; Render(); }
            else { controller.Changed -= Render; displaySelection.Stop(); }
        };
        Render();
    }

    private void ConstrainSize(Microsoft.UI.Windowing.AppWindow sender, Microsoft.UI.Windowing.AppWindowChangedEventArgs args)
    {
        if (!args.DidSizeChange) return;
        var scale = Islands.NativeWindows.GetDpiForWindow(WinRT.Interop.WindowNative.GetWindowHandle(this)) / 96d;
        var width = Math.Max(sender.Size.Width, (int)Math.Ceiling(520 * scale));
        var height = Math.Max(sender.Size.Height, (int)Math.Ceiling(540 * scale));
        if (width != sender.Size.Width || height != sender.Size.Height) sender.Resize(new(width, height));
    }
    private void Render()
    {
        rendering = true;
        try
        {
            VisibilityToggle.IsEnabled = controller.Component is not null;
            VisibilityToggle.IsOn = controller.Component?.IsVisible == true;
            ComponentText.Text = controller.Component is { } component ? $"{component.Text} · {component.StatusLabel}" : "没有有效声明组件";
            IdentityText.Text = controller.Component is { } declared ? string.Join(" / ", declared.Identity.Segments.Select(s => s.Value)) : "无有效声明";
            var state = controller.Session.State switch
            {
                IslandDisplayState.Hidden => "组件已关闭",
                IslandDisplayState.Embedded => "内容岛已嵌入",
                IslandDisplayState.WaitingForTaskbar => "等待任务栏定位条件",
                IslandDisplayState.Failed => "嵌入失败，可重试",
                IslandDisplayState.CleanupPending => "清理待重试",
                _ => "Host 已关闭"
            };
            DockStatusText.Text = $"{state} · {controller.TargetSummary}";
            TargetDetails.Text = controller.Target;
            CommunicationStatusText.Text = controller.CommunicationStatus;
            RetryCommunicationButton.IsEnabled = controller.CanRetryCommunication;
            displaySelection?.Refresh();
            var tests = controller.Tests;
            var active = tests.IsRunning;
            var preview = controller.Preview;
            StartCaseButton.IsEnabled = OpenControlsButton.IsEnabled = !active && !preview.IsOpen && VisibilityToggle.IsOn;
            OpenPreviewButton.IsEnabled = !active && controller.Component is not null;
            OpenPreviewButton.Content = preview.IsOpen ? "应用到预览" : "打开独立预览";
            ClosePreviewButton.IsEnabled = preview.IsOpen;
            PreviewControlsToggle.IsEnabled = !active;
            PreviewPopupButton.IsEnabled = preview.IsAlive && preview.Configuration?.Controls == true;
            PreviewStatusText.Text = preview.Status;
            StopCaseButton.IsEnabled = active;
            MaterialCombo.IsEnabled = ThemeCombo.IsEnabled = LoadCombo.IsEnabled = DurationCombo.IsEnabled = !active;
            AlphaCombo.IsEnabled = !active && MaterialCombo.SelectedIndex == 0;
            StartSamplingButton.IsEnabled = active && !tests.IsSampling && controller.Session.State == IslandDisplayState.Embedded;
            StopSamplingButton.IsEnabled = tests.IsSampling;
            MarkHiddenButton.IsEnabled = MarkRestoredButton.IsEnabled = tests.IsSampling;
            OpenPopupButton.IsEnabled = active && tests.Configuration.Controls && controller.Session.State == IslandDisplayState.Embedded && !controller.PopupOpen;
            ClosePopupButton.IsEnabled = controller.PopupOpen;
            RecordResultButton.IsEnabled = active;
            ExportButton.IsEnabled = tests.LastRun is not null;
            ClearSimulationButton.IsEnabled = controller.SimulateUnavailable;
            SimulateButton.IsEnabled = !controller.SimulateUnavailable;
            RetryButton.IsEnabled = VisibilityToggle.IsOn;
            MaterialStatusText.Text = controller.MaterialStatus;
            CaseStatusText.Text = tests.Status + (active ? $" · {tests.Run!.Configuration.CaseId} · {tests.Run.ElapsedSeconds:F0} 秒" : "");
            ActionHint.Text = preview.IsOpen ? "独立预览已打开：拖动它的标题栏，改参数后应用；关闭预览后可开始任务栏案例。"
                : active ? "案例互斥运行；先记录结果，再停止。"
                : VisibilityToggle.IsOn ? "先开始案例，再采样或记录结果。" : "先打开显示组件，再开始案例或重试。";
            var samples = tests.LastRun?.Samples;
            SamplingText.Text = samples is { Count: > 0 } ? DescribeSamples(samples) : "尚无采样。";
            EvidenceText.Text = tests.EvidenceDirectory + "\n" + controller.Notice;
            HistoryText.Text = string.Join("\n\n", tests.History.Reverse().Select(run =>
                $"{run.StartedAt:HH:mm:ss} · {run.Configuration.CaseId} · {run.Outcome}\n" +
                string.Join("；", run.Results.Where(r => r.Value.At is not null).Select(r => $"{r.Key}: {r.Value.Status}（{r.Value.Note}）")) +
                $"\n{run.DirectoryPath}"));
            ErrorText.Text = string.Join("\n", controller.Errors.Concat(new[] { tests.LastError, displaySelection?.Error }.Where(e => e is not null)));
        }
        finally { rendering = false; }
    }
    private static string DescribeSamples(IReadOnlyList<HostResourceSample> samples)
    {
        var last = samples[^1];
        var cpu = samples.Count > 1 ? 100 * (last.CpuSeconds - samples[^2].CpuSeconds) / (last.Seconds - samples[^2].Seconds) : 0;
        return $"{last.Seconds:F1} 秒 · CPU 单核等效 {cpu:F2}% / 整机 {cpu / Environment.ProcessorCount:F2}%\n私有内存 {last.PrivateBytes / 1048576d:F1} MiB · 工作集 {last.WorkingSetBytes / 1048576d:F1} MiB · 句柄 {last.Handles} · 线程 {last.Threads}\n已更新内容 {last.Updates} 次；不是呈现 FPS。";
    }
    private HostTestConfiguration Selection(bool controls) => new(
        new[] { "none", "acrylic", "mica" }[MaterialCombo.SelectedIndex],
        MaterialCombo.SelectedIndex == 0 ? new[] { 0d, 0.5d, 1d }[AlphaCombo.SelectedIndex] : 1,
        new[] { "system", "light", "dark" }[ThemeCombo.SelectedIndex],
        new[] { 0, 1, 30, 60 }[LoadCombo.SelectedIndex], DurationCombo.SelectedIndex == 0 ? 30 : 1800, controls);
    private void Navigation_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (HostingPanel is null || args.SelectedItem is not NavigationViewItem item) return;
        var panels = new[] { HostingPanel, MaterialPanel, SamplingPanel, EvidencePanel };
        for (var i = 0; i < panels.Length; i++) panels[i].Visibility = item.Tag?.ToString() == i.ToString() ? Visibility.Visible : Visibility.Collapsed;
    }
    private void VisibilityToggle_Toggled(object sender, RoutedEventArgs args) { if (!rendering) controller.Execute(() => controller.SetVisibility(VisibilityToggle.IsOn)); }
    private void TargetDisplayCombo_SelectionChanged(object sender, SelectionChangedEventArgs args) => displaySelection?.SelectionChanged();
    private void RightGapInput_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args) => displaySelection?.GapChanged(args.NewValue);
    private void Material_Changed(object sender, SelectionChangedEventArgs args) { if (AlphaCombo is not null) AlphaCombo.IsEnabled = MaterialCombo.SelectedIndex == 0 && !controller.Tests.IsRunning; }
    private void Retry_Click(object sender, RoutedEventArgs args) => controller.Execute(controller.Retry);
    private async void RetryCommunication_Click(object sender, RoutedEventArgs args) => await controller.RetryCommunicationAsync();
    private void Simulate_Click(object sender, RoutedEventArgs args) => controller.Execute(() => controller.SetSimulation(true));
    private void ClearSimulation_Click(object sender, RoutedEventArgs args) => controller.Execute(() => controller.SetSimulation(false));
    private void StartCase_Click(object sender, RoutedEventArgs args) => controller.Execute(() => controller.Tests.Start(Selection(false)));
    private void OpenControls_Click(object sender, RoutedEventArgs args) => controller.Execute(() => controller.Tests.Start(Selection(true)));
    private void OpenPreview_Click(object sender, RoutedEventArgs args) => controller.Execute(() => controller.OpenPreview(
        WinRT.Interop.WindowNative.GetWindowHandle(this), Selection(PreviewControlsToggle.IsChecked == true)));
    private void ClosePreview_Click(object sender, RoutedEventArgs args) => controller.Execute(controller.Preview.Close);
    private void PreviewPopup_Click(object sender, RoutedEventArgs args) => controller.Execute(() => controller.Preview.SetPopup(true));
    private void StopCase_Click(object sender, RoutedEventArgs args) => controller.Execute(() => controller.Tests.Stop());
    private void OpenPopup_Click(object sender, RoutedEventArgs args) => controller.Execute(() => controller.Tests.SetPopup(true));
    private void ClosePopup_Click(object sender, RoutedEventArgs args) => controller.Execute(() => controller.Tests.SetPopup(false));
    private void StartSampling_Click(object sender, RoutedEventArgs args) => controller.Execute(controller.Tests.StartSampling);
    private void StopSampling_Click(object sender, RoutedEventArgs args) => controller.Execute(controller.Tests.StopSampling);
    private void MarkHidden_Click(object sender, RoutedEventArgs args) => controller.Execute(() => controller.Tests.MarkVisibility(true));
    private void MarkRestored_Click(object sender, RoutedEventArgs args) => controller.Execute(() => controller.Tests.MarkVisibility(false));
    private void RecordResult_Click(object sender, RoutedEventArgs args) => controller.Execute(() => controller.Tests.RecordResult((string)DimensionCombo.SelectedItem, new[] { "passed", "failed", "untested" }[ResultCombo.SelectedIndex], ResultNote.Text));
    private void Export_Click(object sender, RoutedEventArgs args) => controller.Execute(controller.Export);
    private void OpenEvidence_Click(object sender, RoutedEventArgs args) => controller.Execute(controller.OpenEvidence);
}
