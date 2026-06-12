using ECHAT.Models.Events;

namespace ECHAT.Client.Core.Interfaces;

/// <summary>
/// Logica decisionale pura (stateless, snapshot-based) per lo stato delle migrazioni. L'App
/// (<c>MigrationStateTracker</c>) tiene lo stato mutabile (dizionario, set dei locali, eventi,
/// lifetime) e delega a questo manager tutte le decisioni testabili.
/// </summary>
public interface IMigrationStateManager
{
    /// <summary>Mappa lo Status di un job remoto (SignalR) a una fase di migrazione.</summary>
    MigrationPhase MapRemoteStatusToPhase(string status);

    /// <summary>True se la fase è terminale (Completed/Cancelled/Failed).</summary>
    bool IsTerminal(MigrationPhase phase);

    /// <summary>
    /// True se l'evento remoto va ignorato perché la saga è guidata localmente su questo device
    /// (il flusso IProgress locale è più preciso). <paramref name="isLocallyDriven"/> indica se la
    /// conversazione dell'evento è nel set delle saghe locali.
    /// </summary>
    bool ShouldIgnoreRemote(bool isLocallyDriven);

    /// <summary>
    /// Costruisce lo snapshot di progresso da un evento remoto. ProgressPercent finisce in
    /// <see cref="MigrationProgress.Processed"/>; Total resta null (numeri = percentuali).
    /// </summary>
    MigrationProgress BuildRemoteProgress(JobProgressEvent evt);

    /// <summary>
    /// Decide se lo stato terminale può essere ripulito dopo il linger: solo se lo snapshot
    /// corrente coincide ancora (value equality) con quello terminale osservato; altrimenti nel
    /// frattempo è partita una nuova migrazione e non va toccata.
    /// </summary>
    bool ShouldClearTerminal(MigrationProgress? current, MigrationProgress terminalSnapshot);
}
