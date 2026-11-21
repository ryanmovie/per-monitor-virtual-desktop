using System.ComponentModel;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace PerMonitorVD.Overlay;

/// <summary>Captures a bounded, in-memory screen image for a short transition.</summary>
internal static class ScreenCapture
{
    private const uint SourceCopy = 0x00CC0020;
    private const int ColorOnColor = 3;
    private static int _captureInProgress;

    internal static async Task<Bitmap> CaptureWithTimeoutAsync(Func<Bitmap> capture, int timeoutMs = 1200)
    {
        if (Interlocked.CompareExchange(ref _captureInProgress, 1, 0) != 0)
            throw new InvalidOperationException("A previous screen capture is still running.");

        var task = Task.Run(() =>
        {
            try { return capture(); }
            finally { Volatile.Write(ref _captureInProgress, 0); }
        });

        if (await Task.WhenAny(task, Task.Delay(timeoutMs)) != task)
        {
            // A display driver may return after the caller has already fallen back. Dispose
            // that late frame and observe failures without blocking the UI or new switches.
            _ = task.ContinueWith(completed =>
            {
                if (completed.Status == TaskStatus.RanToCompletion)
                    completed.Result.Dispose();
                else
                    _ = completed.Exception;
            }, TaskScheduler.Default);
            throw new TimeoutException("Screen capture timed out.");
        }

        return await task;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr GetDC(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetWindowRect(IntPtr hwnd, out NativeRectangle rectangle);

    private delegate bool MonitorEnumerator(IntPtr monitor, IntPtr dc, ref NativeRectangle rectangle, IntPtr data);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool EnumDisplayMonitors(IntPtr dc, IntPtr clip, MonitorEnumerator callback, IntPtr data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInformation information);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRectangle
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MonitorInformation
    {
        public int Size;
        public NativeRectangle Monitor;
        public NativeRectangle Work;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string Device;
    }

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern bool StretchBlt(IntPtr destination, int x, int y, int width, int height,
        IntPtr source, int sourceX, int sourceY, int sourceWidth, int sourceHeight, uint operation);

    [DllImport("gdi32.dll")]
    private static extern int SetStretchBltMode(IntPtr dc, int mode);

    public static Bitmap Capture(Rectangle bounds, int maximumPixels)
    {
        if (bounds.Width <= 0 || bounds.Height <= 0 || maximumPixels <= 0)
            throw new ArgumentOutOfRangeException(nameof(bounds));

        var scale = Math.Min(1.0, Math.Sqrt(maximumPixels / ((double)bounds.Width * bounds.Height)));
        var width = Math.Max(1, (int)Math.Round(bounds.Width * scale));
        var height = Math.Max(1, (int)Math.Round(bounds.Height * scale));
        var bitmap = new Bitmap(width, height, PixelFormat.Format32bppRgb);
        try
        {
            using var graphics = Graphics.FromImage(bitmap);
            var destination = graphics.GetHdc();
            try
            {
                var source = GetDC(IntPtr.Zero);
                if (source == IntPtr.Zero)
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not get the screen device context.");

                try
                {
                    SetStretchBltMode(destination, ColorOnColor);
                    if (!StretchBlt(destination, 0, 0, width, height, source,
                            bounds.Left, bounds.Top, bounds.Width, bounds.Height, SourceCopy))
                        throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not capture the monitor.");
                }
                finally
                {
                    ReleaseDC(IntPtr.Zero, source);
                }
            }
            finally
            {
                graphics.ReleaseHdc(destination);
            }

            return bitmap;
        }
        catch
        {
            bitmap.Dispose();
            throw;
        }
    }

    internal static Bitmap CaptureWindowBounds(IntPtr hwnd, int maximumPixels)
    {
        return WithPhysicalCoordinates(() =>
        {
            if (!GetWindowRect(hwnd, out var rectangle))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not get the window bounds.");
            return Capture(Rectangle.FromLTRB(rectangle.Left, rectangle.Top, rectangle.Right, rectangle.Bottom), maximumPixels);
        });
    }

    public static Bitmap CaptureMonitorWorkArea(string deviceName, int maximumPixels) =>
        WithPhysicalCoordinates(() => Capture(FindPhysicalMonitorWorkArea(deviceName), maximumPixels));

    internal static Rectangle GetPhysicalMonitorWorkArea(string deviceName) =>
        WithPhysicalCoordinates(() => FindPhysicalMonitorWorkArea(deviceName));

    internal static Rectangle GetPhysicalWindowBounds(IntPtr hwnd) => WithPhysicalCoordinates(() =>
    {
        if (!GetWindowRect(hwnd, out var rectangle))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not get the window bounds.");
        return Rectangle.FromLTRB(rectangle.Left, rectangle.Top, rectangle.Right, rectangle.Bottom);
    });

    private static Rectangle FindPhysicalMonitorWorkArea(string deviceName)
    {
        Rectangle? result = null;
        MonitorEnumerator callback = (IntPtr monitor, IntPtr dc, ref NativeRectangle rectangle, IntPtr data) =>
        {
            var information = new MonitorInformation { Size = Marshal.SizeOf<MonitorInformation>(), Device = "" };
            if (GetMonitorInfo(monitor, ref information) && string.Equals(information.Device, deviceName, StringComparison.OrdinalIgnoreCase))
            {
                result = Rectangle.FromLTRB(information.Work.Left, information.Work.Top, information.Work.Right, information.Work.Bottom);
                return false;
            }
            return true;
        };
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, callback, IntPtr.Zero);
        return result ?? throw new InvalidOperationException($"The display {deviceName} is no longer available.");
    }

    private static T WithPhysicalCoordinates<T>(Func<T> action)
    {
        var previous = SetThreadDpiAwarenessContext(new IntPtr(-4)); // PER_MONITOR_AWARE_V2
        if (previous == IntPtr.Zero)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not use physical monitor coordinates.");
        try { return action(); }
        finally { SetThreadDpiAwarenessContext(previous); }
    }
}
