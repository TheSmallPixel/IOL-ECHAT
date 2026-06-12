using ECHAT.Models.Enums;

namespace ECHAT.Client.Core.Interfaces;

public interface ICustodianWorker
{
    Task RewrapKeysForMemberAsync(Guid conversationId, Guid newMemberDeviceId);
    Task GenerateGapTombstonesAsync(Guid conversationId, long fromSeq, long toSeq);

    /// <summary>
    /// Esegue una saga di strong revoke. Per <see cref="MigrationMode.FullReencrypt"/> il custode
    /// pilota anche il loop di ri-cifrazione. <paramref name="progress"/>, se fornito, riceve
    /// aggiornamenti di fase (Starting, Reencrypting per batch, Finalizing, Completed o Failed);
    /// la UI lo usa per mostrare avanzamento e bloccare l'invio di nuovi messaggi.
    /// </summary>
    Task RunStrongRevokeAsync(
        Guid conversationId,
        MigrationMode mode,
        CancellationToken ct,
        IProgress<MigrationProgress>? progress = null);

    /// <summary>
    /// Forza il finalize di un job FullReencrypt che ha lasciato indietro envelope (perché
    /// il custode non aveva le CEK per decifrarli). Accetta esplicitamente la perdita di
    /// quei messaggi (diventeranno permanentemente illeggibili dopo il crypto-shred).
    /// </summary>
    Task ForceFinalizeAsync(Guid conversationId, Guid jobId);
}
