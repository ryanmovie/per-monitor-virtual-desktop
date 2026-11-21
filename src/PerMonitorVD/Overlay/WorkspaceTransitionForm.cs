using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows.Forms.Integration;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Wpf = System.Windows;
using Media = System.Windows.Media;
using Animation = System.Windows.Media.Animation;

namespace PerMonitorVD.Overlay;

/// <summary>A short-lived, non-activating overlay for one monitor's work area.</summary>
internal sealed class WorkspaceTransitionForm : Form
{
    internal const int MaximumFramePixels = 3840 * 2160;
    private readonly int _direction;
    private readonly int _durationMs;
    private readonly Func<Bitmap> _captureIncoming;
    private readonly Wpf.Controls.Grid _root = new() { ClipToBounds = true, IsHitTestVisible = false };
    private readonly Wpf.Controls.Image _oldImage = new() { Stretch = Media.Stretch.Fill };
    private readonly Wpf.Controls.Image _newImage = new() { Stretch = Media.Stretch.Fill, Visibility = Wpf.Visibility.Hidden };
    private readonly Media.TranslateTransform _oldOffset = new();
    private readonly Media.TranslateTransform _newOffset = new();
    private readonly ElementHost _host = new() { Dock = DockStyle.Fill };
    private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowDisplayAffinity(IntPtr hwnd, uint affinity);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int width, int height, uint flags);

    [DllImport("dwmapi.dll")]
    private static extern int DwmFlush();

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr handle);

    public WorkspaceTransitionForm(Rectangle bounds, string deviceName, Bitmap outgoing, int direction, int durationMs, Func<Bitmap>? captureIncoming = null)
    {
        _captureIncoming = captureIncoming ?? (() => ScreenCapture.CaptureMonitorWorkArea(deviceName, MaximumFramePixels));
        _direction = direction < 0 ? -1 : 1;
        _durationMs = Math.Clamp(durationMs, 100, 1000);
        AutoScaleMode = AutoScaleMode.None;
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = false;
        TopMost = true;
        Bounds = bounds;

        _oldImage.Source = ToBitmapSource(outgoing);
        outgoing.Dispose();
        _oldImage.RenderTransform = _oldOffset;
        _newImage.RenderTransform = _newOffset;
        _root.Children.Add(_oldImage);
        _root.Children.Add(_newImage);
        _host.Child = _root;
        Controls.Add(_host);
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var parameters = base.CreateParams;
            parameters.ExStyle |= 0x08000080; // WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW
            return parameters;
        }
    }

    public async Task ShowCoverAsync(Rectangle expectedBounds)
    {
        _ = Handle;
        if (!SetWindowDisplayAffinity(Handle, 0x11)) // WDA_EXCLUDEFROMCAPTURE
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not exclude the transition overlay from screen captures.");

        Show();
        Bounds = expectedBounds;
        SetWindowPos(Handle, new IntPtr(-1), 0, 0, 0, 0, 0x0013); // topmost, no move/resize/activation
        _root.UpdateLayout();
        await _root.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);
        DwmFlush();

        var actual = new Rectangle(PointToScreen(Point.Empty), ClientSize);
        if (actual != expectedBounds)
            throw new InvalidOperationException($"Transition overlay bounds do not match the selected monitor: expected={expectedBounds}, actual={actual}.");
    }

    public async Task PlayAsync()
    {
        if (IsDisposed || !Visible)
            return;

        DwmFlush();
        using (var incoming = await ScreenCapture.CaptureWithTimeoutAsync(_captureIncoming))
            _newImage.Source = ToBitmapSource(incoming);

        _root.UpdateLayout();
        var width = _root.ActualWidth;
        if (width <= 0)
            return;

        var start = TransitionMath.Offsets(width, 0, _direction);
        var end = TransitionMath.Offsets(width, 1, _direction);
        _newOffset.X = start.Incoming;
        _newImage.Visibility = Wpf.Visibility.Visible;
        var easing = new Animation.CubicEase { EasingMode = Animation.EasingMode.EaseOut };
        easing.Freeze();
        var duration = new Wpf.Duration(TimeSpan.FromMilliseconds(_durationMs));
        var incomingAnimation = new Animation.DoubleAnimation(start.Incoming, end.Incoming, duration) { EasingFunction = easing };
        var outgoingAnimation = new Animation.DoubleAnimation(start.Outgoing, end.Outgoing, duration) { EasingFunction = easing };
        outgoingAnimation.Completed += (_, _) => _completion.TrySetResult();
        _newOffset.BeginAnimation(Media.TranslateTransform.XProperty, incomingAnimation);
        _oldOffset.BeginAnimation(Media.TranslateTransform.XProperty, outgoingAnimation);

        // Never leave the overlay up if a display change or shutdown interrupts animation.
        await Task.WhenAny(_completion.Task, Task.Delay(_durationMs + 1500));
    }

    private static BitmapSource ToBitmapSource(Bitmap bitmap)
    {
        var handle = bitmap.GetHbitmap();
        try
        {
            var source = Imaging.CreateBitmapSourceFromHBitmap(handle, IntPtr.Zero, Wpf.Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            source.Freeze();
            return source;
        }
        finally
        {
            DeleteObject(handle);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _oldOffset.BeginAnimation(Media.TranslateTransform.XProperty, null);
            _newOffset.BeginAnimation(Media.TranslateTransform.XProperty, null);
            _oldImage.Source = null;
            _newImage.Source = null;
            _host.Child = null;
            _completion.TrySetResult();
        }
        base.Dispose(disposing);
    }
}

internal static class TransitionMath
{
    public static int Direction(int oldIndex, int targetIndex, int? requestedDirection = null) =>
        requestedDirection is < 0 ? -1 : requestedDirection is > 0 ? 1 : targetIndex < oldIndex ? -1 : 1;

    public static (double Outgoing, double Incoming) Offsets(double width, double progress, int direction)
    {
        var sign = direction < 0 ? -1 : 1;
        var distance = width * Math.Clamp(progress, 0, 1) * sign;
        return (-distance, width * sign - distance);
    }
}
