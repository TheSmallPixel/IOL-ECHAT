using ECHAT.Client.Core.Interfaces;
using ECHAT.Models.Events;

namespace ECHAT.Client.Core.Services;

/// <summary>
/// Implementazione stateless di <see cref="IMigrationStateManager"/>. Tutte le decisioni sono
/// funzioni pure di input/snapshot: nessuno stato interno, nessun side effect.
/// </summary>
public class MigrationStateManager : IMigrationStateManager
{
    public MigrationPhase MapRemoteStatusToPhase(string status) => status switch
    {
        "Completed" => MigrationPhase.Completed,
        "Cancelled" => MigrationPhase.Cancelled,
        "Failed"    => MigrationPhase.Failed,
        _           => MigrationPhase.Reencrypting
    };

    public bool IsTerminal(MigrationPhase phase)
        => phase == MigrationPhase.Completed
        || phase == MigrationPhase.Cancelled
        || phase == MigrationPhase.Failed;

    public bool ShouldIgnoreRemote(bool isLocallyDriven) => isLocallyDriven;

    public MigrationProgress BuildRemoteProgress(JobProgressEvent evt)
        => new(MapRemoteStatusToPhase(evt.Status), evt.ProgressPercent);

    public bool ShouldClearTerminal(MigrationProgress? current, MigrationProgress terminalSnapshot)
        => current is not null && current == terminalSnapshot;
}
