using ECHAT.Models.Dtos;

namespace ECHAT.Server.Core.Interfaces;

public interface IBlobStorageService
{
    /// <summary>
    /// Apre una sessione di upload per la conversazione indicata. La sessione (token,
    /// conversationId, ownerUserId) viene registrata lato storage così che le successive
    /// scritture di parti e la finalizzazione possano essere validate.
    /// </summary>
    Task<FileUploadSession> BeginUploadAsync(Guid conversationId, Guid ownerUserId);

    /// <summary>
    /// Scrive una parte per un upload in corso. La chiamata è valida solo se
    /// <paramref name="uploadToken"/>, <paramref name="conversationId"/> e
    /// <paramref name="userId"/> corrispondono alla sessione aperta per <paramref name="fileId"/>.
    /// </summary>
    Task StorePartAsync(Guid conversationId, Guid userId, Guid fileId, string uploadToken, int partNo, byte[] encryptedBytes);

    /// <summary>
    /// Finalizza un upload in corso assemblando le parti. Valida la sessione (token, conversazione,
    /// proprietario) prima di scrivere il blob definitivo.
    /// </summary>
    Task<FileCommitResult> FinalizeAsync(Guid conversationId, Guid userId, Guid fileId, string uploadToken);

    /// <summary>
    /// Apre in lettura il blob indicato SOLO se appartiene alla conversazione indicata. In caso di
    /// mismatch (o blob inesistente) viene lanciata <see cref="FileNotFoundException"/> per evitare
    /// un oracolo di esistenza tra conversazioni.
    /// </summary>
    Task<Stream> ReadAsync(Guid conversationId, Guid fileId);

    /// <summary>
    /// Cancella in modo idempotente tutti i blob (e relativi temporanei/parti) di una conversazione.
    /// Invocato dal percorso di cancellazione conversazione DOPO la rimozione delle righe DB, quindi
    /// non dipende dallo stato del database.
    /// </summary>
    Task DeleteConversationAsync(Guid conversationId, IReadOnlyCollection<Guid> fileIds);
}
