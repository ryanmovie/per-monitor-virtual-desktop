using System.Runtime.InteropServices;
using PerMonitorVD.Overlay;

namespace PerMonitorVD.SelfTests;

/// <summary>Optional interactive test. Captures only pixels painted by these test windows.</summary>
internal static class CaptureSmokeTest
{
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowDisplayAffinity(IntPtr hwnd, uint affinity);

    [DllImport("dwmapi.dll")]
    private static extern int DwmFlush();

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int width, int height, uint flags);

    public static int Run()
    {
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        var screen = Screen.PrimaryScreen ?? throw new InvalidOperationException("No primary screen.");
        var bounds = new Rectangle(screen.WorkingArea.Left + 80, screen.WorkingArea.Top + 80, 180, 120);
        var result = 1;

        using var background = new TestWindow(bounds, Color.Lime);
        background.Shown += async (_, _) =>
        {
            try
            {
                SetWindowPos(background.Handle, new IntPtr(-1), 0, 0, 0, 0, 0x0013);
                background.Update();
                DwmFlush();
                await Task.Delay(500);
                var location = background.PointToScreen(new Point(60, 60));
                var before = CaptureAt(location);
                using var cover = new TestWindow(bounds, Color.Red);
                _ = cover.Handle;
                if (!SetWindowDisplayAffinity(cover.Handle, 0x11))
                    throw new InvalidOperationException($"Could not exclude the overlay from capture: {Marshal.GetLastWin32Error()}");
                cover.Show();
                SetWindowPos(cover.Handle, new IntPtr(-1), 0, 0, 0, 0, 0x0013);
                cover.Update();
                DwmFlush();
                background.BackColor = Color.Blue;
                background.Invalidate();
                background.Update();
                DwmFlush();
                await Task.Delay(500);

                var pixel = CaptureAt(location);
                if (before.G < 200 || pixel.B < 200 || pixel.R > 30 || pixel.G > 30)
                    throw new InvalidOperationException($"Capture did not reveal the test background under the excluded overlay: bounds={background.Bounds}; point={location}; before={before}; after={pixel}");

                Console.WriteLine("Capture smoke test passed: an updated background is captured beneath the excluded overlay.");
                result = 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(ex.Message);
            }
            finally
            {
                background.Close();
            }
        };
        Application.Run(background);
        return result;
    }

    private static Color CaptureAt(Point location)
    {
        using var bitmap = ScreenCapture.Capture(new Rectangle(location, new Size(1, 1)), 1);
        return bitmap.GetPixel(0, 0);
    }

    private sealed class TestWindow : Form
    {
        public TestWindow(Rectangle bounds, Color color)
        {
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            ShowInTaskbar = false;
            TopMost = true;
            Bounds = bounds;
            BackColor = color;
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

    }
}
