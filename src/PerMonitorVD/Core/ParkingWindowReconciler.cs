using PerMonitorVD.Utilities;

namespace PerMonitorVD.Core;

/// <summary>Preserves window ownership when conflicting parking desktops are reassigned.</summary>
internal static class ParkingWindowReconciler
{
    public static void Reconcile(
        WorkspaceRuntimeState state,
        IEnumerable<(MonitorState Monitor, WorkspaceSlot Slot, ParkingDesktopAssignment Assignment)> reassignments,
        bool logicalOnly,
        bool fallbackToLogical,
        Func<WindowRecord, bool> isSameWindow,
        Func<WindowRecord, Guid> getDesktop,
        Func<WindowRecord, Guid, string, bool> move,
        Action<WindowRecord, string> hide,
        Action<WindowRecord> restorePlacement)
    {
        foreach (var (monitor, slot, assignment) in reassignments)
        {
            var isCurrent = ReferenceEquals(monitor.Workspaces.ElementAtOrDefault(monitor.CurrentWorkspaceIndex), slot);
            var destination = isCurrent ? state.ActiveCompositeDesktopId : assignment.CurrentId;

            foreach (var window in state.Windows.Values.Where(w =>
                         !w.Ignored && !w.Sticky && !w.HiddenByPmvd &&
                         string.Equals(w.MonitorKey, monitor.MonitorKey, StringComparison.OrdinalIgnoreCase) &&
                         string.Equals(w.WorkspaceId, slot.WorkspaceId, StringComparison.OrdinalIgnoreCase)).ToList())
            {
                // A numeric HWND may now refer to a different application after a restart.
                if (!isSameWindow(window))
                    continue;

                var actualDesktop = getDesktop(window);
                if (actualDesktop != assignment.PreviousId || actualDesktop == destination)
                    continue;

                if (logicalOnly)
                {
                    if (actualDesktop != state.ActiveCompositeDesktopId &&
                        !move(window, state.ActiveCompositeDesktopId, $"repair-logical:{slot.WorkspaceId}"))
                        continue;

                    if (!isCurrent)
                        hide(window, "parking desktop conflict");
                    else if (window.LastPlacement is not null)
                        restorePlacement(window);
                    continue;
                }

                if (move(window, destination, $"repair:{slot.WorkspaceId}"))
                {
                    if (isCurrent && window.LastPlacement is not null)
                        restorePlacement(window);
                    Log.Info($"Rehomed {window.Hwnd} {window.ProcessName} after parking desktop repair.");
                }
                else if (!isCurrent && actualDesktop == state.ActiveCompositeDesktopId && fallbackToLogical)
                {
                    hide(window, "parking desktop repair fallback");
                }
            }
        }
    }
}
