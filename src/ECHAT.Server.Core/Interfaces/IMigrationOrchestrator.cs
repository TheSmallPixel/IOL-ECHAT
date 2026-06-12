using ECHAT.Models.Enums;

namespace ECHAT.Server.Core.Interfaces;

public interface IMigrationOrchestrator
{
    Task<Guid> StartMigrationAsync(Guid conversationId, MigrationMode mode, Guid custodianUserId);

    /// <summary>
    /// Avanza il progresso del job. <paramref name="conversationId"/> è la conversazione della
    /// route: il job DEVE appartenervi, altrimenti viene sollevata <c>NotFoundException</c>.
    /// Questo blocca l'IDOR in cui un admin della propria conversazione passa il jobId di
    /// un'altra conversazione.
    /// </summary>
    Task CheckpointAsync(Guid conversationId, Guid jobId, int batchId, int progressPercent);

    /// <summary>
    /// Finalizza il job (crypto-shred). <paramref name="conversationId"/> è la conversazione
    /// della route: il job DEVE appartenervi, altrimenti <c>NotFoundException</c>.
    /// </summary>
    Task FinalizeAsync(Guid conversationId, Guid jobId);

    /// <summary>
    /// Annulla un job di migrazione. A differenza di <see cref="FinalizeAsync"/> non fa
    /// crypto-shred: gli envelope ri-cifrati finora restano sul nuovo epoch, quelli mai toccati
    /// sul vecchio, quindi entrambe le CEK servono ancora. Emette comunque un evento
    /// <c>JobProgressEvent</c> con Status=Cancelled così le altre tab/admin smettono di mostrare
    /// il banner. Il job DEVE appartenere a <paramref name="conversationId"/>, altrimenti
    /// <c>NotFoundException</c>.
    /// </summary>
    Task CancelAsync(Guid conversationId, Guid jobId);

    /// <summary>
    /// Variante di <see cref="FinalizeAsync"/> che BYPASSA il safety check su CountByEpochBelow.
    /// Procede col crypto-shred anche se restano envelope al vecchio epoch: questi diventano
    /// PERMANENTEMENTE ILLEGGIBILI. Usato quando il custode non ha le CEK per decifrarli
    /// (es. non è Owner e è entrato dopo che quei messaggi erano stati inviati) e accetta
    /// esplicitamente la perdita. Audit-logged dal controller. Il job DEVE appartenere a
    /// <paramref name="conversationId"/>, altrimenti <c>NotFoundException</c>.
    /// </summary>
    Task ForceFinalizeAsync(Guid conversationId, Guid jobId);

    /// <summary>
    /// Job FullReencrypt InProgress per la conversazione (o null). Usato dal guard di
    /// /messages POST e dalla validazione di /messages/{seq}/replace.
    /// </summary>
    Task<MigrationJobRecord?> GetActiveFullReencryptJobAsync(Guid conversationId);

    /// <summary>
    /// Registra una sostituzione fatta dal custode: aggiorna <c>MaxReplacedSeq</c> sul job
    /// (atomicamente) così che <see cref="FinalizeAsync"/> sappia dove tracciare il
    /// ChainBoundary.
    /// </summary>
    Task RecordReplacementAsync(Guid jobId, long seq);
}
