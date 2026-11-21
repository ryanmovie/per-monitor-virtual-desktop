using System.Diagnostics;
using System.Runtime.InteropServices;
using PerMonitorVD.Overlay;
using Media = System.Windows.Media;

namespace PerMonitorVD.SelfTests;

/// <summary>Optional interactive test using small temporary synthetic windows.</summary>
internal static class TransitionSmokeTest
{
    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();
    [DllImport("dwmapi.dll")]
    private static extern int DwmFlush();

    public static int Run(bool secondary)
    {
        Application.SetHighDpiMode(HighDpiMode.SystemAware);
        var screen = secondary ? Screen.AllScreens.FirstOrDefault(s => !s.Primary) : Screen.PrimaryScreen;
        if (screen is null) throw new InvalidOperationException("Requested screen was not found.");
        var bounds = new Rectangle(screen.WorkingArea.Left + 150, screen.WorkingArea.Top + 150, 240, 150);
        var result = 1;
        using var background = new TestWindow(bounds);
        background.Shown += async (_, _) =>
        {
            try
            {
                SetWindowPos(background.Handle, new IntPtr(-1), 0, 0, 0, 0, 0x0013);
                background.Update();
                DwmFlush();
                await Task.Delay(400);
                var actual = new Rectangle(background.PointToScreen(Point.Empty), background.ClientSize);
                var backgroundHandle = background.Handle;
                AssertMonitorPixel(screen.DeviceName, backgroundHandle, Color.Lime);

                var outgoing = ScreenCapture.CaptureWindowBounds(backgroundHandle, 3840 * 2160);
                using var transition = new WorkspaceTransitionForm(actual, screen.DeviceName, outgoing, 1, 240,
                    () => ScreenCapture.CaptureWindowBounds(backgroundHandle, 3840 * 2160));
                await transition.ShowCoverAsync(actual);
                if (GetForegroundWindow() == transition.Handle)
                    throw new InvalidOperationException("The transition took keyboard focus.");

                background.BackColor = Color.Blue;
                background.Invalidate();
                background.Update();
                DwmFlush();
                await Task.Delay(300);
                AssertMonitorPixel(screen.DeviceName, backgroundHandle, Color.Blue);

                var frames = 0;
                EventHandler onRender = (_, _) => frames++;
                Media.CompositionTarget.Rendering += onRender;
                var watch = Stopwatch.StartNew();
                try { await transition.PlayAsync(); }
                finally { Media.CompositionTarget.Rendering -= onRender; }
                watch.Stop();
                if (watch.ElapsedMilliseconds < 120 || watch.ElapsedMilliseconds > 2000 || frames < 2)
                    throw new InvalidOperationException($"Animation timing or frames were unexpected: {watch.ElapsedMilliseconds} ms, {frames} frames.");

                Console.WriteLine($"Transition smoke test passed on {screen.DeviceName}: {watch.ElapsedMilliseconds} ms, {frames} frames, WPF render tier {Media.RenderCapability.Tier >> 16}.");
                result = 0;
            }
            catch (Exception ex) { Console.Error.WriteLine(ex); }
            finally { background.Close(); }
        };
        Application.Run(background);
        return result;
    }

    private static void AssertMonitorPixel(string deviceName, IntPtr window, Color expected)
    {
        var workArea = ScreenCapture.GetPhysicalMonitorWorkArea(deviceName);
        var windowBounds = ScreenCapture.GetPhysicalWindowBounds(window);
        using var image = ScreenCapture.CaptureMonitorWorkArea(deviceName, 3840 * 2160);
        var x = (int)((windowBounds.Left + windowBounds.Width / 2.0 - workArea.Left) * image.Width / workArea.Width);
        var y = (int)((windowBounds.Top + windowBounds.Height / 2.0 - workArea.Top) * image.Height / workArea.Height);
        if (x < 0 || y < 0 || x >= image.Width || y >= image.Height)
            throw new InvalidOperationException("The test window is outside the captured monitor area.");
        var actual = image.GetPixel(x, y);
        if (Math.Abs(actual.R - expected.R) > 30 || Math.Abs(actual.G - expected.G) > 30 || Math.Abs(actual.B - expected.B) > 30)
            throw new InvalidOperationException($"The test window was not correctly captured on {deviceName}: expected={expected}, actual={actual}, workArea={workArea}, window={windowBounds}.");
    }

    private sealed class TestWindow : Form
    {
        public TestWindow(Rectangle bounds)
        {
            AutoScaleMode = AutoScaleMode.None;
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            ShowInTaskbar = false;
            TopMost = true;
            Bounds = bounds;
            BackColor = Color.Lime;
        }

        protected override bool ShowWithoutActivation => true;
        protected override CreateParams CreateParams
        {
            get
            {
                var parameters = base.CreateParams;
                parameters.ExStyle |= 0x08000080;
                return parameters;
            }
        }
    }
}
