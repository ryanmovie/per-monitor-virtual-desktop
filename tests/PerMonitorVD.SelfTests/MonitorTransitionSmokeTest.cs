using System.Diagnostics;
using PerMonitorVD.Overlay;
using Media = System.Windows.Media;

namespace PerMonitorVD.SelfTests;

/// <summary>Optional interactive performance check. Captured frames stay in memory.</summary>
internal static class MonitorTransitionSmokeTest
{
    public static int Run(bool secondary)
    {
        Application.SetHighDpiMode(HighDpiMode.SystemAware);
        var screen = secondary ? Screen.AllScreens.FirstOrDefault(s => !s.Primary) : Screen.PrimaryScreen;
        if (screen is null) throw new InvalidOperationException("Requested screen was not found.");
        var result = 1;
        using var context = new ApplicationContext();
        using var timer = new System.Windows.Forms.Timer { Interval = 100 };
        timer.Tick += async (_, _) =>
        {
            timer.Stop();
            try
            {
                var captureTimer = Stopwatch.StartNew();
                var outgoing = await Task.Run(() => ScreenCapture.CaptureMonitorWorkArea(screen.DeviceName, 3840 * 2160));
                captureTimer.Stop();
                using var transition = new WorkspaceTransitionForm(screen.WorkingArea, screen.DeviceName, outgoing, 1, 240);
                await transition.ShowCoverAsync(screen.WorkingArea);
                var frames = 0;
                EventHandler onRender = (_, _) => frames++;
                Media.CompositionTarget.Rendering += onRender;
                var animationTimer = Stopwatch.StartNew();
                try { await transition.PlayAsync(); }
                finally { Media.CompositionTarget.Rendering -= onRender; }
                animationTimer.Stop();
                if (animationTimer.ElapsedMilliseconds > 2500 || frames < 6)
                    throw new InvalidOperationException($"Monitor animation produced too few frames or timed out: {animationTimer.ElapsedMilliseconds} ms, {frames} frames.");
                Console.WriteLine($"Monitor transition passed on {screen.DeviceName}: first capture {captureTimer.ElapsedMilliseconds} ms; capture and animation {animationTimer.ElapsedMilliseconds} ms; {frames} rendered frames; WPF tier {Media.RenderCapability.Tier >> 16}.");
                result = 0;
            }
            catch (Exception ex) { Console.Error.WriteLine(ex); }
            finally { context.ExitThread(); }
        };
        timer.Start();
        Application.Run(context);
        return result;
    }
}
