using ECHAT.Models.Domain;
using ECHAT.Models.Enums;

namespace ECHAT.Client.Core.Interfaces;

public interface IChatSdk
{
    Task SendMessageAsync(Guid conversationId, string text, List<AttachmentRef>? attachments = null);
    Task<List<DecryptedMessage>> FetchMessagesAsync(Guid conversationId, long? afterSeq, long? beforeSeq, int limit);

    /// <summary>Nasconde/ri-mostra un messaggio (moderazione). Richiede ModerateMessages lato server.</summary>
    Task ModerateMessageAsync(Guid conversationId, long seq, bool hidden, string? reason);
    Task<Guid> UploadFileAsync(Guid conversationId, Stream fileStream, string fileName, string mimeType);
    Task<Stream> DownloadFileAsync(Guid conversationId, Guid fileId);
    Task AddMemberAsync(Guid conversationId, Guid userId, bool includeHistory);

    /// <summary>
    /// E2EE (S1): genera la CEK dell'<paramref name="epochId"/> lato client, la wrappa per i device
    /// dei membri e la posta. Va chiamato dal creatore subito dopo aver creato la conversazione
    /// (epoch 1): il server non genera più CEK.
    /// </summary>
    Task ProvisionConversationKeysAsync(Guid conversationId, int epochId);

    /// <summary>
    /// E2EE (S1): ri-wrappa la CEK corrente per tutti i device dei membri (grant). Va chiamato
    /// dopo un add-member, così i device del nuovo membro ricevono la chiave.
    /// </summary>
    Task GrantConversationKeysAsync(Guid conversationId);

    /// <summary>Cambia il ruolo ("Admin"/"Member") di un membro (Owner only lato server).</summary>
    Task SetMemberRoleAsync(Guid conversationId, Guid userId, string role);

    /// <summary>Rinomina la conversazione (Owner/Admin lato server).</summary>
    Task RenameConversationAsync(Guid conversationId, string newName);

    /// <summary>Cancella definitivamente la conversazione: crypto-shred (Owner/Admin lato server).</summary>
    Task DeleteConversationAsync(Guid conversationId);

    /// <summary>
    /// Pre-riserva <paramref name="count"/> seq dal server in una sola chiamata HTTP. Ottimizza
    /// i batch (multi-file upload) collassando N reservation in una sola. Vedi
    /// <c>MessageFlowOrchestrator.PreReserveSeqsAsync</c>.
    /// </summary>
    Task PreReserveSeqsAsync(Guid conversationId, int count);

    /// <summary>
    /// Force-finalize di un job FullReencrypt bloccato perché il custode non ha le CEK per
    /// alcuni vecchi envelope. Accetta esplicitamente la perdita: quegli envelope diventeranno
    /// illeggibili dopo il crypto-shred.
    /// </summary>
    Task ForceFinalizeMigrationAsync(Guid conversationId, Guid jobId);

    /// <summary>
    /// Removes <paramref name="userId"/> from the conversation. The server always rotates the
    /// epoch and shreds the removed member's wrapped CEKs; the client then provisions the new
    /// CEK for the remaining members (null/<see cref="MigrationMode.RewrapOnly"/> stop here;
    /// no server-side job). <see cref="MigrationMode.FullReencrypt"/> additionally drives the
    /// custodian loop that decrypts every old envelope and re-encrypts it under the new CEK.
    /// <paramref name="progress"/> riceve aggiornamenti di fase per la UI (banner + lockout
    /// del composer); rilevante solo per FullReencrypt, ignorato per gli altri modi.
    /// </summary>
    Task RemoveMemberAsync(
        Guid conversationId,
        Guid userId,
        MigrationMode? migration,
        IProgress<MigrationProgress>? progress = null,
        CancellationToken ct = default);
}
