using System;
using Microsoft.UI;
using Microsoft.UI.Composition;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Hosting;
using Mtp.Platform.Core;
using Windows.Graphics;
using WinRT;

namespace Mtp.Host;

/// <summary>
/// The compact single-row renderer shared by the probe, top-level control and 05A dock.
/// It is prepared hidden; the adapter positions and shows it through Win32 after reparenting,
/// so no AppWindow call other than Close may run once the window has a foreign parent.
/// </summary>
public sealed partial class ExplorerTaskbarProbeWindow : Window
{
    // One shared Compositor for every brush this process creates. Creating a new one per brush
    // instance and dropping it on each material switch leaked compositors and crashed the process.
    private static readonly Windows.UI.Composition.Compositor SharedCompositor = new();

    private TransparentBackdrop? transparentBackdrop;
    private ChildColorBrush? childBrush;
    private DesktopAcrylicController? acrylicController;
    private MicaController? micaController;
    private SystemBackdropConfiguration? acrylicConfiguration;
    private bool initialized;

    internal event EventHandler? BackdropConnectionChanged;
    internal bool IsBackdropConnected => childBrush?.IsConnected == true || transparentBackdrop?.IsConnected == true;

    internal bool TryApplyChildMaterial(double opacity, uint colorRgb, out string? detail)
    {
        try
        {
            var alpha = (byte)Math.Clamp(Math.Round(MaterialSpec.ClampOpacity(opacity) * 255), 0, 255);
            var compositor = ElementCompositionPreview.GetElementVisual(RootGrid).Compositor;
            var color = Windows.UI.Color.FromArgb(alpha, (byte)(colorRgb >> 16), (byte)(colorRgb >> 8), (byte)colorRgb);
            var brush = new ChildColorBrush(compositor, color,
                () => BackdropConnectionChanged?.Invoke(this, EventArgs.Empty));
            RootGrid.Background = brush;
            childBrush = brush;
            RequiresSurfacePaint = true;
            detail = $"child composition color brush requested; argb=({alpha},{color.R},{color.G},{color.B})";
            BackdropConnectionChanged?.Invoke(this, EventArgs.Empty);
            return true;
        }
        catch (Exception exception)
        {
            detail = $"{FormatException(exception)} HRESULT=0x{exception.HResult:X8}";
            return false;
        }
    }

    public ExplorerTaskbarProbeWindow(HostComponentDisplayModel display)
        : this()
    {
        InitializeView(display);
    }

    internal ExplorerTaskbarProbeWindow()
    {
        Closed += (_, _) => ReleaseAcrylicController();
    }

    internal void InitializeView(HostComponentDisplayModel display)
    {
        ArgumentNullException.ThrowIfNull(display);
        if (!initialized)
        {
            InitializeComponent();
            initialized = true;
        }

        SetDisplay(display);
    }

    /// <summary>
    /// Reports which materials the current Windows build can actually render. Mica needs Windows 11;
    /// the solid brush needs no system support because it is drawn by this process.
    /// </summary>
    public static MaterialCapabilities ProbeCapabilities() => new(
        SupportsSolid: true,
        SupportsAcrylic: SafeIsSupported(static () => DesktopAcrylicController.IsSupported()),
        SupportsMica: SafeIsSupported(static () => MicaController.IsSupported()));

    private static bool SafeIsSupported(Func<bool> probe)
    {
        try
        {
            return probe();
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// Applies a resolved material. <paramref name="spec"/> must already be the effective value
    /// returned by <see cref="MaterialResolver"/>; this method does not resolve or downgrade again.
    /// </summary>
    public bool TryApplyMaterial(MaterialSpec spec, out string? detail)
    {
        // The controller path and the backdrop-brush path are mutually exclusive: attaching both
        // leaves whichever was applied first still driving the surface, which is why switching
        // materials used to look like the previous material with only a tint change.
        ClearMaterial();

        switch (spec.Kind)
        {
            case MaterialKind.Acrylic:
                return TryApplyAcrylicController(spec.Opacity, out detail);

            case MaterialKind.Mica:
                return TryApplyMicaController(spec.Opacity, out detail);

            case MaterialKind.Solid:
                // The tested solid path requires both alpha on the brush and one window-DC paint.
                // The underlying cause of alpha loss remains unverified.
                var alpha = (byte)Math.Clamp(Math.Round(spec.Opacity * 255), 0, 255);
                if (!TryApplyTransparentBackdrop(Windows.UI.Color.FromArgb(alpha, 0x20, 0x20, 0x20), out var solidFailure))
                {
                    detail = solidFailure;
                    return false;
                }

                RequiresSurfacePaint = true;
                detail = $"solid argb({alpha},32,32,32)";
                return true;

            default:
                detail = "none (opaque surface)";
                return true;
        }
    }

    /// <summary>
    /// True when the current material needs one GDI paint on the window DC before its alpha is
    /// honoured. The window itself holds no Win32 calls, so the caller (which owns the adapter
    /// boundary) performs the paint and clears this flag.
    /// </summary>
    public bool RequiresSurfacePaint { get; private set; }

    /// <summary>Called by the adapter boundary after it has painted the window surface once.</summary>
    public void MarkSurfacePainted() => RequiresSurfacePaint = false;

    /// <summary>Removes any attached material so the window falls back to an opaque surface.</summary>
    public void ClearMaterial()
    {
        if (childBrush is not null)
        {
            RootGrid.Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0, 0, 0, 0));
            childBrush = null;
        }
        SystemBackdrop = null;
        transparentBackdrop = null;
        RequiresSurfacePaint = false;
        ReleaseAcrylicController();
    }

    /// <summary>
    /// The controller path proven by DeskBox on this machine: a thin acrylic with zero tint, so only
    /// the blur remains. Unlike the transparent brush it always shows some material.
    /// </summary>
    public bool TryApplyAcrylicController(out string? detail) => TryApplyAcrylicController(0.8, out detail);

    private bool TryApplyAcrylicController(double opacity, out string? detail)
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
                LuminosityOpacity = (float)MaterialSpec.ClampOpacity(opacity),
            };
            acrylicController.AddSystemBackdropTarget(this.As<ICompositionSupportsSystemBackdrop>());
            acrylicController.SetSystemBackdropConfiguration(acrylicConfiguration);
            detail = string.Create(System.Globalization.CultureInfo.InvariantCulture, $"acrylic thin tint=0 luminosity={opacity:F2}");
            return true;
        }
        catch (Exception exception)
        {
            ReleaseAcrylicController();
            detail = FormatException(exception);
            return false;
        }
    }

    private bool TryApplyMicaController(double opacity, out string? detail)
    {
        try
        {
            if (!MicaController.IsSupported())
            {
                detail = "MicaController not supported";
                return false;
            }

            ReleaseAcrylicController();
            acrylicConfiguration = new SystemBackdropConfiguration
            {
                IsInputActive = true,
                Theme = RootGrid.ActualTheme == ElementTheme.Dark ? SystemBackdropTheme.Dark : SystemBackdropTheme.Light,
            };
            micaController = new MicaController
            {
                Kind = MicaKind.BaseAlt,
                LuminosityOpacity = (float)MaterialSpec.ClampOpacity(opacity),
            };
            micaController.AddSystemBackdropTarget(this.As<ICompositionSupportsSystemBackdrop>());
            micaController.SetSystemBackdropConfiguration(acrylicConfiguration);
            detail = string.Create(System.Globalization.CultureInfo.InvariantCulture, $"mica baseAlt luminosity={opacity:F2}");
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

        try
        {
            micaController?.Dispose();
        }
        catch (Exception)
        {
            // The composition target may already be gone.
        }

        acrylicController = null;
        micaController = null;
        acrylicConfiguration = null;
    }

    public nint WindowHandle => WinRT.Interop.WindowNative.GetWindowHandle(this);

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
            transparentBackdrop = new TransparentBackdrop(tint, () => BackdropConnectionChanged?.Invoke(this, EventArgs.Empty));
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
    public string DescribeBackdropState() => childBrush is not null ? childBrush.IsConnected ? "child composition color brush connected" : "child composition color brush pending" : transparentBackdrop switch
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
    /// The brush alone is not enough: the window surface must also be made DWM glass by the adapter,
    /// and for <see cref="MaterialKind.Solid"/> the adapter must additionally paint the window DC once
    /// (see <c>Win32ExplorerTaskbarEmbedAdapter.PaintWindowSurfaceOnce</c>) to keep alpha honoured.
    /// No Win32 call lives here; that boundary is enforced by the platform-skeleton tests.
    /// </summary>
    private sealed class ChildColorBrush(Microsoft.UI.Composition.Compositor compositor, Windows.UI.Color color, Action changed) : XamlCompositionBrushBase
    {
        public bool IsConnected { get; private set; }
        protected override void OnConnected()
        {
            CompositionBrush = compositor.CreateColorBrush(color);
            IsConnected = true;
            changed();
        }

        protected override void OnDisconnected()
        {
            var brush = CompositionBrush;
            CompositionBrush = null;
            brush?.Dispose();
            IsConnected = false;
            changed();
        }
    }

    private sealed partial class TransparentBackdrop(Windows.UI.Color tint, Action connectionChanged) : SystemBackdrop
    {
        private readonly Windows.UI.Composition.Compositor compositor = SharedCompositor;
        private readonly Windows.UI.Color tint = tint;
        private Windows.UI.Composition.CompositionColorBrush? brush;

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
            connectionChanged();
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
