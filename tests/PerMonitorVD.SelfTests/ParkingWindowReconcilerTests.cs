using PerMonitorVD.Core;

internal static class ParkingWindowReconcilerTests
{
    public static void Run()
    {
        ActiveCollisionOnlyMovesOwnedWindows();
        CurrentWorkspaceReturnsToActiveDesktop();
        FailedMovesHideOnlyWindowsOnActiveDesktop();
        LogicalModeReturnsThenHidesInactiveWindows();
    }

    private static void ActiveCollisionOnlyMovesOwnedWindows()
    {
        var h = new Harness(current: false, oldIsActive: true);
        var owned = h.Window("owned", h.Slot.WorkspaceId, h.Active);
        var other = h.Window("other", "display-w1", h.Active);
        var stale = h.Window("stale", h.Slot.WorkspaceId, h.Active);
        var sticky = h.Window("sticky", h.Slot.WorkspaceId, h.Active);
        var ignored = h.Window("ignored", h.Slot.WorkspaceId, h.Active);
        var alreadyHidden = h.Window("hidden", h.Slot.WorkspaceId, h.Active);
        h.Stale.Add(stale);
        sticky.Sticky = true;
        ignored.Ignored = true;
        alreadyHidden.HiddenByPmvd = true;

        h.Reconcile();

        Check(h.Desktops[owned] == h.NewParking && h.Moved.Count == 1, "Only the reassigned workspace's valid managed window should move.");
        Check(h.Desktops[other] == h.Active && h.Desktops[stale] == h.Active, "Other workspaces and reused window handles must be left alone.");
        Check(h.Hidden.Count == 0, "Successful repair should not hide any windows.");
    }

    private static void CurrentWorkspaceReturnsToActiveDesktop()
    {
        var h = new Harness(current: true, oldIsActive: false);
        var window = h.Window("current", h.Slot.WorkspaceId, h.OldParking);
        window.LastPlacement = new WindowPlacementSnapshot();

        h.Reconcile();

        Check(h.Desktops[window] == h.Active && h.Restored.SequenceEqual([window]), "A current workspace's window must return to active and recover its placement.");
    }

    private static void FailedMovesHideOnlyWindowsOnActiveDesktop()
    {
        var onActive = new Harness(current: false, oldIsActive: true) { MovesSucceed = false };
        var visible = onActive.Window("visible", onActive.Slot.WorkspaceId, onActive.Active);
        onActive.Reconcile();
        Check(onActive.Hidden.SequenceEqual([visible]), "A failed move from active should use the configured hide fallback.");

        var parked = new Harness(current: false, oldIsActive: false) { MovesSucceed = false };
        var offDesktop = parked.Window("parked", parked.Slot.WorkspaceId, parked.OldParking);
        parked.Reconcile();
        Check(parked.Hidden.Count == 0 && parked.Desktops[offDesktop] == parked.OldParking, "A failed move from another desktop must not falsely mark that window as hidden on active.");

        var fallbackOff = new Harness(current: false, oldIsActive: true) { MovesSucceed = false, FallbackToLogical = false };
        fallbackOff.Window("visible", fallbackOff.Slot.WorkspaceId, fallbackOff.Active);
        fallbackOff.Reconcile();
        Check(fallbackOff.Hidden.Count == 0, "Disabling logical fallback must be respected.");
    }

    private static void LogicalModeReturnsThenHidesInactiveWindows()
    {
        var h = new Harness(current: false, oldIsActive: false) { LogicalOnly = true };
        var window = h.Window("logical", h.Slot.WorkspaceId, h.OldParking);

        h.Reconcile();

        Check(h.Desktops[window] == h.Active && h.Hidden.SequenceEqual([window]), "Logical mode must return the inactive window to active before hiding it.");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class Harness
    {
        public Guid Active { get; } = Guid.NewGuid();
        public Guid OldParking { get; }
        public Guid NewParking { get; } = Guid.NewGuid();
        public WorkspaceSlot Slot { get; } = new() { WorkspaceId = "display-w2" };
        public WorkspaceRuntimeState State { get; } = new();
        public MonitorState Monitor { get; }
        public Dictionary<WindowRecord, Guid> Desktops { get; } = [];
        public HashSet<WindowRecord> Stale { get; } = [];
        public List<WindowRecord> Moved { get; } = [];
        public List<WindowRecord> Hidden { get; } = [];
        public List<WindowRecord> Restored { get; } = [];
        public bool MovesSucceed { get; set; } = true;
        public bool LogicalOnly { get; set; }
        public bool FallbackToLogical { get; set; } = true;

        public Harness(bool current, bool oldIsActive)
        {
            OldParking = oldIsActive ? Active : Guid.NewGuid();
            Slot.ParkingDesktopId = NewParking;
            Monitor = new MonitorState
            {
                MonitorKey = "display",
                CurrentWorkspaceIndex = current ? 1 : 0,
                Workspaces = [new WorkspaceSlot { WorkspaceId = "display-w1" }, Slot]
            };
            State.ActiveCompositeDesktopId = Active;
            State.Monitors.Add(Monitor);
        }

        public WindowRecord Window(string id, string workspace, Guid desktop)
        {
            var window = new WindowRecord { Hwnd = id, MonitorKey = "display", WorkspaceId = workspace };
            State.Windows[id] = window;
            Desktops[window] = desktop;
            return window;
        }

        public void Reconcile() => ParkingWindowReconciler.Reconcile(
            State,
            [(Monitor, Slot, new ParkingDesktopAssignment(OldParking, NewParking, true))],
            LogicalOnly, FallbackToLogical,
            window => !Stale.Contains(window),
            window => Desktops[window],
            (window, destination, _) =>
            {
                if (!MovesSucceed) return false;
                Desktops[window] = destination;
                Moved.Add(window);
                return true;
            },
            (window, _) => Hidden.Add(window),
            window => Restored.Add(window));
    }
}
