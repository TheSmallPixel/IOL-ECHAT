namespace ECHAT.Client.Core.Interfaces;

/// <summary>
/// Notifica di stato emessa da <see cref="ICustodianWorker.RunStrongRevokeAsync"/> via
/// <see cref="IProgress{T}"/>. La UI usa <see cref="Phase"/> per disabilitare il composer,
/// <see cref="Processed"/>/<see cref="Total"/> per mostrare la percentuale (Total può essere
/// null per i modi che non lo calcolano).
///
/// <see cref="Skipped"/> conta gli envelope che il custode non ha potuto ri-cifrare (CEK
/// mancante per quell'epoch, oppure decrypt fallito). Sono envelope ancora al vecchio epoch:
/// se ne resta uno solo al finalize, il server rifiuta lo shred (vedi safety check in
/// MigrationOrchestratorService.FinalizeAsync) per non rendere quei messaggi illeggibili.
/// </summary>
public record MigrationProgress(
    MigrationPhase Phase,
    int Processed = 0,
    int? Total = null,
    int Skipped = 0,
    string? Error = null);

/// <summary>
/// Saga fallita perché il custode non è riuscito a ri-cifrare tutti gli envelope (manca la
/// CEK per qualche vecchio epoch). La UI riconosce questa eccezione e mostra il bottone
/// "Force finalize (lose N messages)".
/// </summary>
public class MigrationIncompleteException : Exception
{
    public Guid JobId { get; }
    public int RemainingEnvelopes { get; }
    public MigrationIncompleteException(Guid jobId, int remaining, string message) : base(message)
    {
        JobId = jobId;
        RemainingEnvelopes = remaining;
    }
}

public enum MigrationPhase
{
    /// <summary>Saga avviata, prima di iniziare i batch.</summary>
    Starting,
    /// <summary>Loop di ri-cifrazione attivo. <c>Processed</c> = envelope scritti finora.</summary>
    Reencrypting,
    /// <summary>Loop concluso, in attesa di FinalizeMigration (crypto-shred lato server).</summary>
    Finalizing,
    /// <summary>Tutto completato.</summary>
    Completed,
    /// <summary>Saga annullata via Cancel button. Diverso da Failed: non c'è un errore, e
    /// gli envelope ri-cifrati finora restano col nuovo epoch (no rollback).</summary>
    Cancelled,
    /// <summary>Saga fallita. <c>Error</c> contiene il messaggio.</summary>
    Failed
}
