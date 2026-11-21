using PerMonitorVD.Core;
using PerMonitorVD.Configuration;
using PerMonitorVD.Native;
using PerMonitorVD.Overlay;

namespace PerMonitorVD.SelfTests;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            if (args.Contains("--capture-smoke"))
                return CaptureSmokeTest.Run();
            if (args.Contains("--transition-smoke"))
                return TransitionSmokeTest.Run(secondary: false);
            if (args.Contains("--transition-smoke-secondary"))
                return TransitionSmokeTest.Run(secondary: true);
            if (args.Contains("--transition-monitor-smoke"))
                return MonitorTransitionSmokeTest.Run(secondary: false);
            if (args.Contains("--transition-monitor-smoke-secondary"))
                return MonitorTransitionSmokeTest.Run(secondary: true);

            ActiveDesktopCollisionGetsANewParkingDesktop();
            DuplicateParkingDesktopsBecomeDistinct();
            NamedDesktopClaimedByAnotherWorkspaceIsNotStolen();
            ExistingDistinctDesktopIsReused();
            MissingActiveDesktopIsRejected();
            InvalidCreatedDesktopIsRejected();
            ParkingWindowReconcilerTests.Run();
            TransitionDirectionAndFramesAreCorrect();
            ExistingConfigurationsRetainDefaults();
            Console.WriteLine("All workspace state and transition tests passed.");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            return 1;
        }
    }

    private static void ActiveDesktopCollisionGetsANewParkingDesktop()
    {
        var active = Guid.NewGuid();
        var slot = Slot("DISPLAY1-W1", active);
        var bridge = new FakeBridge(new NativeDesktopInfo(active, "[PMVD] DISPLAY1-W1 (4)"));
        var allocator = new ParkingDesktopAllocator(bridge, bridge.Desktops, active, [slot], true);
        var assignment = allocator.Assign(slot, "[PMVD] DISPLAY1-W1 (4)");
        Check(slot.ParkingDesktopId != active, "The active desktop must not be assigned as parking.");
        Check(assignment.Changed && assignment.PreviousExists && assignment.PreviousId == active, "The previous active collision must be reported for window recovery.");
        Check(bridge.Created.Count == 1, "A separate parking desktop should be created.");
    }

    private static void DuplicateParkingDesktopsBecomeDistinct()
    {
        var active = Guid.NewGuid();
        var shared = Guid.NewGuid();
        var a = Slot("DISPLAY1-W1", shared);
        var b = Slot("DISPLAY2-W1", shared);
        var bridge = new FakeBridge(new NativeDesktopInfo(active, "ACTIVE"), new NativeDesktopInfo(shared, "old"));
        var allocator = new ParkingDesktopAllocator(bridge, bridge.Desktops, active, [a, b], true);
        allocator.Assign(a, "A");
        var reassigned = allocator.Assign(b, "B");
        Check(a.ParkingDesktopId == shared && b.ParkingDesktopId != shared, "Duplicate slots must be assigned distinct desktops.");
        Check(reassigned.Changed && reassigned.PreviousExists, "The duplicated desktop must be reported for window recovery.");
    }

    private static void NamedDesktopClaimedByAnotherWorkspaceIsNotStolen()
    {
        var active = Guid.NewGuid();
        var claimed = Guid.NewGuid();
        var a = Slot("DISPLAY1-W1", Guid.Empty);
        var b = Slot("DISPLAY2-W1", claimed);
        var bridge = new FakeBridge(new NativeDesktopInfo(active, "ACTIVE"), new NativeDesktopInfo(claimed, "A"));
        var allocator = new ParkingDesktopAllocator(bridge, bridge.Desktops, active, [a, b], true);
        allocator.Assign(a, "A");
        allocator.Assign(b, "B");
        Check(a.ParkingDesktopId != claimed && b.ParkingDesktopId == claimed, "A named desktop already owned by a later workspace must be preserved.");
    }

    private static void ExistingDistinctDesktopIsReused()
    {
        var active = Guid.NewGuid();
        var parked = Guid.NewGuid();
        var slot = Slot("DISPLAY1-W1", parked);
        var bridge = new FakeBridge(new NativeDesktopInfo(active, "ACTIVE"), new NativeDesktopInfo(parked, "old"));
        var allocator = new ParkingDesktopAllocator(bridge, bridge.Desktops, active, [slot], true);
        var assignment = allocator.Assign(slot, "new");
        Check(slot.ParkingDesktopId == parked && !assignment.Changed && bridge.Created.Count == 0, "Valid assignments should be kept.");
    }

    private static void MissingActiveDesktopIsRejected()
    {
        var bridge = new FakeBridge();
        ExpectThrows(() => new ParkingDesktopAllocator(bridge, bridge.Desktops, Guid.NewGuid(), [], true), "A missing active desktop must be rejected.");
    }

    private static void InvalidCreatedDesktopIsRejected()
    {
        var active = Guid.NewGuid();
        var bridge = new FakeBridge(new NativeDesktopInfo(active, "ACTIVE")) { NextCreatedId = active };
        var allocator = new ParkingDesktopAllocator(bridge, bridge.Desktops, active, [], true);
        ExpectThrows(() => allocator.Assign(Slot("DISPLAY1-W1", Guid.Empty), "A"), "The bridge must not return the active desktop as a newly created parking desktop.");
    }

    private static void TransitionDirectionAndFramesAreCorrect()
    {
        Check(TransitionMath.Direction(2, 0, 1) == 1, "Next across the wrap must slide forward.");
        Check(TransitionMath.Direction(0, 2, -1) == -1, "Previous across the wrap must slide backward.");
        Check(TransitionMath.Direction(2, 0) == -1, "Direct selection should use the destination direction.");
        Check(TransitionMath.Offsets(100, 0, 1) == (0, 100), "The next workspace must begin to the right.");
        Check(TransitionMath.Offsets(100, 1, 1) == (-100, 0), "The outgoing workspace must end to the left.");
        Check(TransitionMath.Offsets(100, 0, -1) == (0, -100), "The previous workspace must begin to the left.");
        Check(TransitionMath.Offsets(100, 1, -1) == (100, 0), "The outgoing workspace must end to the right.");
    }

    private static void ExistingConfigurationsRetainDefaults()
    {
        var config = new ConfigStore().Deserialize("{\"EnableWinCtrlOverride\":true}");
        Check(config.EnableWorkspaceAnimation && config.WorkspaceAnimationDurationMs == 240, "Older configurations should receive animation defaults.");
        Check(config.Rules.Any(r => r.Process == "explorer.exe" && r.Mode == WindowRuleMode.Ignore), "File Explorer must remain excluded by default.");
        var copy = new AppConfig();
        config.EnableWorkspaceAnimation = false;
        config.WorkspaceAnimationDurationMs = 500;
        copy.CopyFrom(config);
        Check(!copy.EnableWorkspaceAnimation && copy.WorkspaceAnimationDurationMs == 500, "Live configuration reload must copy animation settings.");
    }

    private static WorkspaceSlot Slot(string name, Guid id) => new() { WorkspaceId = name, ParkingDesktopId = id };
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static void ExpectThrows(Action action, string message)
    {
        try { action(); }
        catch (InvalidOperationException) { return; }
        throw new InvalidOperationException(message);
    }

    private sealed class FakeBridge(params NativeDesktopInfo[] desktops) : IVirtualDesktopBridge
    {
        public List<NativeDesktopInfo> Desktops { get; } = [.. desktops];
        public List<NativeDesktopInfo> Created { get; } = [];
        public Guid? NextCreatedId { get; set; }
        public NativeDesktopInfo GetCurrentDesktop() => Desktops[0];
        public IReadOnlyList<NativeDesktopInfo> GetDesktops() => Desktops;
        public NativeDesktopInfo CreateDesktop(string name)
        {
            var created = new NativeDesktopInfo(NextCreatedId ?? Guid.NewGuid(), name);
            Created.Add(created);
            Desktops.Add(created);
            return created;
        }
        public void RenameDesktop(Guid desktopId, string name) { }
        public void SwitchToDesktop(Guid desktopId) => throw new NotSupportedException();
        public Guid GetWindowDesktopId(IntPtr hwnd) => throw new NotSupportedException();
        public bool MoveWindowToDesktop(IntPtr hwnd, Guid desktopId) => throw new NotSupportedException();
    }
}
