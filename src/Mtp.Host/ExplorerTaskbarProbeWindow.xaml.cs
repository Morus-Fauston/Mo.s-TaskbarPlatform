using System;
using Microsoft.UI;
using Microsoft.UI.Composition;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Mtp.Platform.Core;
using Windows.Graphics;
using WinRT;

namespace Mtp.Host;

/// <summary>
/// The compact single-row window used only by the Explorer taskbar embed probe.
/// It is prepared hidden; the adapter positions and shows it through Win32 after reparenting,
/// so no AppWindow call other than Close may run once the window has a foreign parent.
/// </summary>
public sealed partial class ExplorerTaskbarProbeWindow : Window
{
    private TransparentBackdrop? transparentBackdrop;
    private DesktopAcrylicController? acrylicController;
    private SystemBackdropConfiguration? acrylicConfiguration;

    public ExplorerTaskbarProbeWindow(HostComponentDisplayModel display)
    {
        ArgumentNullException.ThrowIfNull(display);
        InitializeComponent();
        SetDisplay(display);
        Closed += (_, _) => ReleaseAcrylicController();
    }

    /// <summary>
    /// The controller path proven by DeskBox on this machine: a thin acrylic with zero tint, so only
    /// the blur remains. Unlike the transparent brush it always shows some material.
    /// </summary>
    public bool TryApplyAcrylicController(out string? detail)
    {
        try
        {
            if (!DesktopAcrylicController.IsSupported())
            {
                detail = "DesktopAcrylicController not supported";
                return false;
            }

            ReleaseAcrylicController();
            acrylicConfiguration = new SystemBackdropConfiguration
            {
                IsInputActive = true,
                Theme = RootGrid.ActualTheme == ElementTheme.Dark ? SystemBackdropTheme.Dark : SystemBackdropTheme.Light,
            };
            acrylicController = new DesktopAcrylicController
            {
                Kind = DesktopAcrylicKind.Thin,
                TintOpacity = 0f,
                LuminosityOpacity = 0.1f,
            };
            acrylicController.AddSystemBackdropTarget(this.As<ICompositionSupportsSystemBackdrop>());
            acrylicController.SetSystemBackdropConfiguration(acrylicConfiguration);
            detail = "acrylic thin tint=0 luminosity=0.1";
            return true;
        }
        catch (Exception exception)
        {
            ReleaseAcrylicController();
            detail = FormatException(exception);
            return false;
        }
    }

    private void ReleaseAcrylicController()
    {
        try
        {
            acrylicController?.Dispose();
        }
        catch (Exception)
        {
            // The composition target may already be gone.
        }

        acrylicController = null;
        acrylicConfiguration = null;
    }

    public nint WindowHandle => Win32Interop.GetWindowFromWindowId(AppWindow.Id);

    public void SetDisplay(HostComponentDisplayModel display)
    {
        ArgumentNullException.ThrowIfNull(display);
        ComponentText.Text = display.Text;
        StatusText.Text = display.StatusLabel;
        ComponentText.Opacity = display.Status == CapabilityStatus.Failed ? 0.75 : 1;
    }

    public void PrepareHidden(SizeInt32 pixelSize) => PrepareHidden(pixelSize, useToolWindowPresenter: true);

    /// <summary>
    /// The embed path keeps the tool-window presenter; the top-level control path uses the default
    /// presenter configured exactly like the TransparencyLab baseline (border and title bar off only).
    /// </summary>
    public void PrepareHidden(SizeInt32 pixelSize, bool useToolWindowPresenter)
    {
        var presenter = useToolWindowPresenter
            ? OverlappedPresenter.CreateForToolWindow()
            : AppWindow.Presenter as OverlappedPresenter ?? OverlappedPresenter.Create();
        presenter.IsAlwaysOnTop = false;
        presenter.IsResizable = false;
        presenter.IsMaximizable = false;
        presenter.IsMinimizable = false;
        presenter.SetBorderAndTitleBar(false, false);
        if (useToolWindowPresenter || !ReferenceEquals(AppWindow.Presenter, presenter))
        {
            AppWindow.SetPresenter(presenter);
        }

        AppWindow.Resize(pixelSize);
    }

    /// <summary>
    /// Control experiment only. Presenter changes re-apply window styles, so this must run before the
    /// transparency stack is applied. DeskBox keeps its widgets non-topmost and manages z-order itself,
    /// so the control window is not marked always-on-top either.
    /// </summary>
    public void PrepareAsTopLevelControl()
    {
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsAlwaysOnTop = false;
        }

        AppWindow.IsShownInSwitchers = false;
    }

    public void ShowAt(PointInt32 position)
    {
        AppWindow.Move(position);
        Activate();
    }

    public bool TryApplyTransparentBackdrop(out string? failureDetail) =>
        TryApplyTransparentBackdrop(Windows.UI.Color.FromArgb(0, 0, 0, 0), out failureDetail);

    /// <summary>
    /// Transparency is a visual goal, not a probe success condition; a failure here is reported
    /// as a step detail and the probe continues with an opaque background.
    /// </summary>
    public bool TryApplyTransparentBackdrop(Windows.UI.Color tint, out string? failureDetail)
    {
        try
        {
            transparentBackdrop = new TransparentBackdrop(tint);
            SystemBackdrop = transparentBackdrop;
            failureDetail = null;
            return true;
        }
        catch (Exception exception)
        {
            transparentBackdrop = null;
            failureDetail = FormatException(exception);
            return false;
        }
    }

    /// <summary>
    /// "created" only proves the brush exists; "connected" proves XAML handed it a composition target.
    /// </summary>
    public string DescribeBackdropState() => transparentBackdrop switch
    {
        null when acrylicController is not null => "acrylic controller attached",
        null => "not applied",
        { ConnectFailure: string failure } => $"connect failed: {failure}",
        { IsConnected: true } => "connected",
        _ => "created, not yet connected",
    };

    private static string FormatException(Exception exception)
    {
        var message = exception.Message;
        var lineBreak = message.IndexOfAny(['\r', '\n']);
        if (lineBreak >= 0)
        {
            message = message[..lineBreak];
        }

        return $"{exception.GetType().Name}: {message.Trim()}";
    }

    /// <summary>
    /// The backdrop target accepts a system-compositor brush (Windows.UI.Composition), so the
    /// brush is created eagerly here where the caller can still record a failure as a probe step.
    /// The brush alone is not enough: the window surface must also be made DWM glass by the adapter.
    /// </summary>
    private sealed partial class TransparentBackdrop : SystemBackdrop
    {
        private readonly Windows.UI.Composition.Compositor compositor;
        private readonly Windows.UI.Color tint;
        private Windows.UI.Composition.CompositionColorBrush? brush;

        public TransparentBackdrop(Windows.UI.Color tint)
        {
            this.tint = tint;
            compositor = new Windows.UI.Composition.Compositor();
            brush = compositor.CreateColorBrush(tint);
        }

        public bool IsConnected { get; private set; }

        public string? ConnectFailure { get; private set; }

        protected override void OnTargetConnected(ICompositionSupportsSystemBackdrop connectedTarget, XamlRoot xamlRoot)
        {
            base.OnTargetConnected(connectedTarget, xamlRoot);
            try
            {
                brush ??= compositor.CreateColorBrush(tint);
                connectedTarget.SystemBackdrop = brush;
                IsConnected = true;
            }
            catch (Exception exception)
            {
                // A failure inside the XAML callback must not take the Host down; the window stays opaque.
                ConnectFailure = FormatException(exception);
            }
        }

        protected override void OnTargetDisconnected(ICompositionSupportsSystemBackdrop disconnectedTarget)
        {
            try
            {
                disconnectedTarget.SystemBackdrop = null;
            }
            catch (Exception)
            {
                // The target may already be gone when Explorer destroys the child window.
            }

            brush?.Dispose();
            brush = null;
            IsConnected = false;
            base.OnTargetDisconnected(disconnectedTarget);
        }
    }
}