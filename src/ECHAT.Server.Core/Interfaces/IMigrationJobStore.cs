namespace ECHAT.Server.Core.Interfaces;

public interface IMigrationJobStore
{
    /// <summary>True se esiste un job NON terminale (Completed/Cancelled/Failed) per la conversazione.</summary>
    Task<bool> HasActiveJobAsync(Guid conversationId);

    Task CreateAsync(MigrationJobRecord job);
    Task<MigrationJobRecord?> GetByIdAsync(Guid jobId);

    /// <summary>
    /// Persist i campi mutabili del job. Solleva <see cref="Microsoft.EntityFrameworkCore.DbUpdateConcurrencyException"/>
    /// se lo Status sul DB è cambiato dall'ultima Read (un'altra transazione ha vinto la corsa).
    /// </summary>
    Task SaveAsync(MigrationJobRecord job);

    /// <summary>
    /// Restituisce il job FullReencrypt InProgress per la conversazione, o null se non c'è.
    /// Usato sia dal guard di /messages POST sia da /messages/{seq}/replace per validare il custode.
    /// </summary>
    Task<MigrationJobRecord?> GetActiveFullReencryptJobAsync(Guid conversationId);

    /// <summary>
    /// Sposta atomicamente <c>MaxReplacedSeq</c> al massimo tra il valore attuale e
    /// <paramref name="seq"/>. Chiamato dalla pipeline di replace.
    /// </summary>
    Task UpdateMaxReplacedSeqAsync(Guid jobId, long seq);
}

public class MigrationJobRecord
{
    public Guid Id { get; init; }
    public Guid ConversationId { get; init; }
    public string Mode { get; init; } = "FullReencrypt";
    public string Status { get; set; } = "InProgress";
    public int ProgressPercent { get; set; }
    public int? LastCheckpointBatchId { get; set; }
    public DateTime CreatedAt { get; init; }
    public DateTime? CompletedAt { get; set; }

    /// <summary>L'utente che ha avviato il job (= custode autorizzato a /replace per FullReencrypt).</summary>
    public Guid CustodianUserId { get; init; }

    /// <summary>Max seq sostituito durante la saga; serve per la riga ChainBoundary al finalize.</summary>
    public long MaxReplacedSeq { get; set; }
}
