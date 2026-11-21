using System.Runtime.InteropServices;
using PerMonitorVD.Utilities;

namespace PerMonitorVD.Overlay;

public sealed class OverlayService
{
    private readonly SynchronizationContext? _syncContext;
    private readonly Dictionary<string, OverlayForm> _forms = new(StringComparer.OrdinalIgnoreCase);

    public OverlayService(SynchronizationContext? syncContext)
    {
        _syncContext = syncContext;
    }

    [DllImport("user32.dll")]
    private static extern bool SystemParametersInfo(uint action, uint parameter, out int value, uint flags);

    internal async Task<WorkspaceTransitionForm?> BeginTransitionAsync(string monitorKey, int direction, int durationMs)
    {
        if (_syncContext is null)
            return null;

        try
        {
            return await InvokeOnUiThreadAsync(() => BeginTransitionOnUiThreadAsync(monitorKey, direction, durationMs));
        }
        catch (Exception ex)
        {
            Log.Warn($"Desktop animation could not start; switching normally: {ex.Message}");
            return null;
        }
    }

    private async Task<WorkspaceTransitionForm?> BeginTransitionOnUiThreadAsync(string monitorKey, int direction, int durationMs)
    {
        // Respect Windows' reduced-animation preference. An unavailable system setting does
        // not stop a normal workspace switch.
        if (SystemParametersInfo(0x1042, 0, out var animationsEnabled, 0) && animationsEnabled == 0)
            return null;

        var screen = Screen.AllScreens.FirstOrDefault(s => string.Equals(Normalize(s.DeviceName), monitorKey, StringComparison.OrdinalIgnoreCase));
        if (screen is null || screen.WorkingArea.Width <= 0 || screen.WorkingArea.Height <= 0)
            return null;

        if (_forms.TryGetValue(monitorKey, out var indicator) && !indicator.IsDisposed)
            indicator.Hide();

        Bitmap? outgoing = null;
        WorkspaceTransitionForm? transition = null;
        try
        {
            outgoing = await ScreenCapture.CaptureWithTimeoutAsync(() => ScreenCapture.CaptureMonitorWorkArea(screen.DeviceName, WorkspaceTransitionForm.MaximumFramePixels));
            transition = new WorkspaceTransitionForm(screen.WorkingArea, screen.DeviceName, outgoing, direction, durationMs);
            outgoing = null; // The form owns the frame from this point on.
            await transition.ShowCoverAsync(screen.WorkingArea);
            return transition;
        }
        catch
        {
            transition?.Dispose();
            outgoing?.Dispose();
            throw;
        }
    }

    internal async Task EndTransitionAsync(WorkspaceTransitionForm? transition, bool animate)
    {
        if (transition is null || _syncContext is null)
            return;

        try
        {
            await InvokeOnUiThreadAsync(async () =>
            {
                try
                {
                    if (animate)
                        await transition.PlayAsync();
                }
                finally
                {
                    transition.Dispose();
                }
                return true;
            });
        }
        catch (Exception ex)
        {
            Log.Warn($"Desktop animation ended early; workspace switching continues: {ex.Message}");
        }
    }

    private Task<T> InvokeOnUiThreadAsync<T>(Func<Task<T>> action)
    {
        if (_syncContext is null)
            throw new InvalidOperationException("The UI synchronization context is unavailable.");
        if (SynchronizationContext.Current == _syncContext)
            return action();

        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        _syncContext.Post(async _ =>
        {
            try { completion.TrySetResult(await action()); }
            catch (Exception ex) { completion.TrySetException(ex); }
        }, null);
        return completion.Task;
    }

    public void ShowWorkspace(string monitorKey, string label, bool enabled)
    {
        if (!enabled) return;
        ShowOnUiThread(() =>
        {
            var screen = Screen.AllScreens.FirstOrDefault(s => Normalize(s.DeviceName) == monitorKey) ?? Screen.PrimaryScreen;
            ShowOnScreen(screen, label);
        });
    }

    public void ShowText(string text, bool enabled)
    {
        if (!enabled) return;
        ShowOnUiThread(() => ShowOnScreen(Screen.PrimaryScreen, text));
    }

    private void ShowOnScreen(Screen? screen, string text)
    {
        if (screen is null) return;

        var key = Normalize(screen.DeviceName);
        if (!_forms.TryGetValue(key, out var form) || form.IsDisposed)
        {
            form = new OverlayForm();
            _forms[key] = form;
        }

        form.ShowText(screen.WorkingArea, text);
    }

    private void ShowOnUiThread(Action action)
    {
        if (_syncContext is null)
        {
            action();
            return;
        }

        _syncContext.Post(_ => action(), null);
    }

    private static string Normalize(string value) => value.Replace("\\\\.\\", "", StringComparison.OrdinalIgnoreCase).Trim();
}

internal sealed class OverlayForm : Form
{
    private readonly Label _label;
    private readonly System.Windows.Forms.Timer _timer;

    public OverlayForm()
    {
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = false;
        TopMost = true;
        BackColor = Color.Black;
        Opacity = 0.78;

        _label = new Label
        {
            Dock = DockStyle.Fill,
            ForeColor = Color.White,
            BackColor = Color.Transparent,
            Font = new Font(FontFamily.GenericSansSerif, 42, FontStyle.Bold),
            TextAlign = ContentAlignment.MiddleCenter
        };
        Controls.Add(_label);

        _timer = new System.Windows.Forms.Timer { Interval = 700 };
        _timer.Tick += (_, _) => Hide();
    }

    public void ShowText(Rectangle workingArea, string text)
    {
        _label.Text = text;
        Size = new Size(260, 140);
        Location = new Point(
            workingArea.Left + (workingArea.Width - Width) / 2,
            workingArea.Top + (workingArea.Height - Height) / 2);

        _timer.Stop();
        Show();
        BringToFront();
        _timer.Start();
    }
}
