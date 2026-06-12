using System.ComponentModel.DataAnnotations;

namespace ECHAT.Server.App.Data.Entities;

public class MigrationJobEntity
{
    public Guid Id { get; set; }
    public Guid ConversationId { get; set; }
    public string Mode { get; set; } = string.Empty;

    /// <summary>
    /// [ConcurrencyCheck] include Status nella WHERE delle UPDATE EF Core. Due transazioni
    /// concorrenti che leggono lo stesso job a Status="InProgress" e poi provano a portarlo
    /// in stati terminali diversi (es. Cancel vs Finalize) non possono più vincere entrambe:
    /// la seconda UPDATE affetterà 0 righe e solleverà DbUpdateConcurrencyException, che gli
    /// orchestratori interpretano come "qualcun altro mi ha preceduto" (idempotente).
    /// </summary>
    [ConcurrencyCheck]
    public string Status { get; set; } = "Requested";

    public int ProgressPercent { get; set; }
    public int? LastCheckpointBatchId { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? CompletedAt { get; set; }

    /// <summary>
    /// L'utente che ha avviato la migrazione e che (per FullReencrypt) è autorizzato a chiamare
    /// /messages/{seq}/replace. <see cref="Guid.Empty"/> per job creati prima di questa colonna.
    /// </summary>
    public Guid CustodianUserId { get; set; }

    /// <summary>
    /// Seq più alto sostituito durante FullReencrypt: usato per scrivere il ChainBoundary al
    /// finalize. 0 finché nessun replace è avvenuto.
    /// </summary>
    public long MaxReplacedSeq { get; set; }
}
