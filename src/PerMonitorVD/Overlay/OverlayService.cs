using System.Runtime.InteropServices;
using PerMonitorVD.Utilities;

namespace PerMonitorVD.Overlay;

public sealed class OverlayService
{
    private readonly SynchronizationContext? _syncContext;
    private readonly Dictionary<string, WorkspaceTransitionForm> _transitions = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Bitmap> _previewFrames = new(StringComparer.OrdinalIgnoreCase);
    private Task _animationTask = Task.CompletedTask;
    private readonly Dictionary<string, OverlayForm> _forms = new(StringComparer.OrdinalIgnoreCase);

    public OverlayService(SynchronizationContext? syncContext)
    {
        _syncContext = syncContext;
    }

    [DllImport("user32.dll")]
    private static extern bool SystemParametersInfo(uint action, uint parameter, out int value, uint flags);

    internal async Task<WorkspaceTransitionForm?> BeginTransitionAsync(
        string monitorKey,
        int direction,
        int durationMs,
        string outgoingWorkspaceId,
        string incomingWorkspaceId)
    {
        if (_syncContext is null)
            return null;

        try
        {
            return await InvokeOnUiThreadAsync(() => BeginTransitionOnUiThreadAsync(
                monitorKey, direction, durationMs, outgoingWorkspaceId, incomingWorkspaceId));
        }
        catch (Exception ex)
        {
            Log.Warn($"Desktop animation could not start; switching normally: {ex.Message}");
            return null;
        }
    }

    private async Task<WorkspaceTransitionForm?> BeginTransitionOnUiThreadAsync(
        string monitorKey,
        int direction,
        int durationMs,
        string outgoingWorkspaceId,
        string incomingWorkspaceId)
    {
        // Respect Windows' reduced-animation preference. An unavailable system setting does
        // not stop a normal workspace switch.
        if (SystemParametersInfo(0x1042, 0, out var animationsEnabled, 0) && animationsEnabled == 0)
            return null;

        var screen = Screen.AllScreens.FirstOrDefault(s => string.Equals(Normalize(s.DeviceName), monitorKey, StringComparison.OrdinalIgnoreCase));
        if (screen is null || screen.WorkingArea.Width <= 0 || screen.WorkingArea.Height <= 0)
            return null;
        var display = screen;

        if (_forms.TryGetValue(monitorKey, out var indicator) && !indicator.IsDisposed)
            indicator.Hide();

        Bitmap? outgoing = null;
        Bitmap? incoming = null;
        WorkspaceTransitionForm? transition = null;
        try
        {
            if (!_animationTask.IsCompleted)
            {
                if (_transitions.TryGetValue(monitorKey, out var active))
                    active.Cancel();
                await _animationTask;
            }

            outgoing = CopyPreviewFrame(monitorKey, outgoingWorkspaceId) ??
                       await ScreenCapture.CaptureWithTimeoutAsync(() => ScreenCapture.CaptureMonitorWorkArea(display.DeviceName, WorkspaceTransitionForm.MaximumFramePixels));
            incoming = CopyPreviewFrame(monitorKey, incomingWorkspaceId) ?? new Bitmap(outgoing);
            if (_transitions.TryGetValue(monitorKey, out var existing) && !existing.IsDisposed)
            {
                existing.PrepareFrame(outgoing, incoming, direction, durationMs);
                transition = existing;
            }
            else
            {
                transition = new WorkspaceTransitionForm(display.WorkingArea, display.DeviceName, outgoing, incoming, direction, durationMs);
                _transitions[monitorKey] = transition;
            }
            outgoing = null; // The form owns the frame from this point on.
            incoming = null;
            await transition.ShowCoverAsync(display.WorkingArea);
            return transition;
        }
        catch
        {
            _transitions.Remove(monitorKey);
            transition?.Dispose();
            outgoing?.Dispose();
            incoming?.Dispose();
            throw;
        }
    }

    internal Task EndTransitionAsync(
        WorkspaceTransitionForm? transition,
        bool animate,
        string monitorKey,
        string workspaceId,
        bool refreshPreview)
    {
        if (transition is null)
        {
            if (refreshPreview)
                RefreshPreview(monitorKey, workspaceId);
            return Task.CompletedTask;
        }

        if (_syncContext is null)
            return Task.CompletedTask;

        try
        {
            if (!animate)
            {
                return InvokeOnUiThreadAsync(() =>
                {
                    transition.Hide();
                    transition.ClearFrame();
                    if (refreshPreview)
                        RefreshPreview(monitorKey, workspaceId);
                    return Task.FromResult(true);
                });
            }

            _animationTask = PlayTransitionAsync(transition, monitorKey, workspaceId, refreshPreview);
        }
        catch (Exception ex)
        {
            Log.Warn($"Desktop animation ended early; workspace switching continues: {ex.Message}");
        }

        return Task.CompletedTask;
    }

    private async Task PlayTransitionAsync(
        WorkspaceTransitionForm transition,
        string monitorKey,
        string workspaceId,
        bool refreshPreview)
    {
        try
        {
            await InvokeOnUiThreadAsync(async () =>
            {
                try
                {
                    await transition.PlayAsync();
                }
                finally
                {
                    transition.Hide();
                    transition.ClearFrame();
                    if (refreshPreview)
                        RefreshPreview(monitorKey, workspaceId);
                }
                return true;
            });
        }
        catch (Exception ex)
        {
            Log.Warn($"Desktop animation ended early; workspace switching continues: {ex.Message}");
        }
    }

    private Bitmap? CopyPreviewFrame(string monitorKey, string workspaceId)
    {
        return _previewFrames.TryGetValue(PreviewKey(monitorKey, workspaceId), out var frame)
            ? new Bitmap(frame)
            : null;
    }

    private void RefreshPreview(string monitorKey, string workspaceId)
    {
        var screen = Screen.AllScreens.FirstOrDefault(s => string.Equals(Normalize(s.DeviceName), monitorKey, StringComparison.OrdinalIgnoreCase));
        if (screen is null)
            return;
        var deviceName = screen.DeviceName;

        _ = Task.Run(() => ScreenCapture.CaptureMonitorWorkArea(deviceName, WorkspaceTransitionForm.MaximumFramePixels))
            .ContinueWith(task =>
            {
                if (!task.IsCompletedSuccessfully)
                {
                    Log.Warn($"Could not refresh transition preview for {monitorKey}: {task.Exception?.GetBaseException().Message}");
                    return;
                }

                var frame = task.Result;
                if (_syncContext is null)
                {
                    frame.Dispose();
                    return;
                }

                _syncContext.Post(_ =>
                {
                    if (_previewFrames.TryGetValue(PreviewKey(monitorKey, workspaceId), out var previous))
                        previous.Dispose();
                    _previewFrames[PreviewKey(monitorKey, workspaceId)] = frame;
                }, null);
            }, TaskScheduler.Default);
    }

    internal void WarmUp(IEnumerable<(string MonitorKey, string WorkspaceId)> previews)
    {
        foreach (var (monitorKey, workspaceId) in previews)
        {
            var screen = Screen.AllScreens.FirstOrDefault(s => string.Equals(Normalize(s.DeviceName), monitorKey, StringComparison.OrdinalIgnoreCase));
            if (screen is not null && (!_transitions.TryGetValue(monitorKey, out var existing) || existing.IsDisposed))
            {
                try
                {
                    _transitions[monitorKey] = new WorkspaceTransitionForm(
                        screen.WorkingArea, screen.DeviceName, new Bitmap(1, 1), 1, 240);
                }
                catch (Exception ex)
                {
                    Log.Warn($"Could not warm up transition overlay for {monitorKey}: {ex.Message}");
                }
            }
            RefreshPreview(monitorKey, workspaceId);
        }
    }

    private static string PreviewKey(string monitorKey, string workspaceId) => $"{monitorKey}|{workspaceId}";

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
