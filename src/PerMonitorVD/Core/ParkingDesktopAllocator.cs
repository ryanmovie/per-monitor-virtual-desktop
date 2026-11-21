using PerMonitorVD.Native;
using PerMonitorVD.Utilities;

namespace PerMonitorVD.Core;

/// <summary>Assigns distinct native parking desktops without reusing the active desktop.</summary>
internal sealed class ParkingDesktopAllocator
{
    private readonly IVirtualDesktopBridge _bridge;
    private readonly List<NativeDesktopInfo> _desktops;
    private readonly HashSet<Guid> _used;
    private readonly Dictionary<Guid, HashSet<WorkspaceSlot>> _claims;
    private readonly bool _rename;

    public ParkingDesktopAllocator(
        IVirtualDesktopBridge bridge,
        IReadOnlyList<NativeDesktopInfo> desktops,
        Guid activeDesktopId,
        IEnumerable<WorkspaceSlot> previousSlots,
        bool rename)
    {
        if (activeDesktopId == Guid.Empty || !desktops.Any(d => d.Id == activeDesktopId))
            throw new InvalidOperationException("The active composite desktop does not exist.");

        _bridge = bridge;
        _desktops = desktops.ToList();
        _used = [activeDesktopId];
        _rename = rename;
        _claims = previousSlots
            .Where(slot => slot.ParkingDesktopId != Guid.Empty)
            .GroupBy(slot => slot.ParkingDesktopId)
            .ToDictionary(group => group.Key, group => group.ToHashSet());
    }

    public ParkingDesktopAssignment Assign(WorkspaceSlot slot, string name)
    {
        var previousId = slot.ParkingDesktopId;
        var previousExists = previousId != Guid.Empty && _desktops.Any(d => d.Id == previousId);
        NativeDesktopInfo? selected = null;
        var created = false;

        if (previousExists && !_used.Contains(previousId))
            selected = _desktops.First(d => d.Id == previousId);

        if (selected is null)
        {
            // Do not take a desktop already recorded for another workspace that has not been
            // visited yet. This also prevents the active desktop being reused by its old name.
            selected = _desktops.FirstOrDefault(d =>
                d.Id != Guid.Empty && !_used.Contains(d.Id) &&
                string.Equals(d.Name, name, StringComparison.OrdinalIgnoreCase) &&
                (!_claims.TryGetValue(d.Id, out var owners) || owners.Contains(slot)));

            if (selected is null)
            {
                selected = _bridge.CreateDesktop(name);
                if (selected.Id == Guid.Empty || _used.Contains(selected.Id) || _desktops.Any(d => d.Id == selected.Id))
                    throw new InvalidOperationException("Creating a distinct parking desktop failed.");

                _desktops.Add(selected);
                created = true;
                Log.Info($"Created parking desktop {name} {selected.Id}");
            }
        }

        _used.Add(selected.Id);
        slot.ParkingDesktopId = selected.Id;
        if (!created && _rename && !string.Equals(selected.Name, name, StringComparison.Ordinal))
            _bridge.RenameDesktop(selected.Id, name);

        if (previousId != Guid.Empty && previousId != selected.Id)
            Log.Warn($"Reassigned parking desktop for {slot.WorkspaceId}: {previousId} -> {selected.Id}");

        return new ParkingDesktopAssignment(previousId, selected.Id, previousExists);
    }
}

internal readonly record struct ParkingDesktopAssignment(Guid PreviousId, Guid CurrentId, bool PreviousExists)
{
    public bool Changed => PreviousId != Guid.Empty && PreviousId != CurrentId;
}
