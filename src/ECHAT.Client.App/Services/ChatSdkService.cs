using ECHAT.Client.Core.Interfaces;
using ECHAT.Client.Core.Services;
using ECHAT.Models.Domain;
using ECHAT.Models.Enums;

namespace ECHAT.Client.App.Services;

/// <summary>
/// Implementazione di <see cref="IChatSdk"/>: è un thin wrapper che lega lo strato
/// HTTP dell'App al flusso dei messaggi orchestrato in Client.Core
/// (<see cref="MessageFlowOrchestrator"/>) e ai metodi file che dipendono da JS interop
/// e dall'uploader a chunk dell'App.
/// </summary>
public class ChatSdkService : IChatSdk
{
    private readonly MessageFlowOrchestrator _flow;
    private readonly IChatServerGateway _gateway;
    private readonly IFileTransferManager _files;
    private readonly ICustodianWorker _custodian;
    private readonly IMigrationStateTracker _migrations;
    private readonly FileEncryptionOrchestrator _fileOrchestrator;
    private readonly CekProvisioner _cekProvisioner;

    public ChatSdkService(
        MessageFlowOrchestrator flow,
        IChatServerGateway gateway,
        IFileTransferManager files,
        ICustodianWorker custodian,
        IMigrationStateTracker migrations,
        FileEncryptionOrchestrator fileOrchestrator,
        CekProvisioner cekProvisioner)
    {
        _flow = flow;
        _gateway = gateway;
        _files = files;
        _custodian = custodian;
        _migrations = migrations;
        _fileOrchestrator = fileOrchestrator;
        _cekProvisioner = cekProvisioner;
    }

    /// <summary>
    /// Single chokepoint usato da tutti i path che spediscono messaggi (SendMessage, SendFile,
    /// AI auto-reply via SendMessage). Se c'è un FullReencrypt in corso per la conversazione
    /// tiriamo subito: così nemmeno l'AiAutoReplyService che gira out-of-band riesce ad
    /// appoggiare un envelope sul vecchio epoch mentre il custode lo sta ri-scrivendo.
    /// </summary>
    private void GuardAgainstActiveMigration(Guid conversationId)
    {
        if (_migrations.IsActive(conversationId))
            throw new InvalidOperationException(
                "Cannot send: a key migration is in progress for this conversation. Try again when it completes.");
    }

    public void SetCek(Guid conversationId, int epochId, byte[] cek)
        => _flow.SetCek(conversationId, epochId, cek);

    public Task SendMessageAsync(Guid conversationId, string text, List<AttachmentRef>? attachments = null)
    {
        GuardAgainstActiveMigration(conversationId);
        return _flow.SendMessageAsync(conversationId, text, MessageFormat.Plain, attachments);
    }

    public Task SendMessageAsync(
        Guid conversationId, string text, MessageFormat format, List<AttachmentRef>? attachments = null)
    {
        GuardAgainstActiveMigration(conversationId);
        return _flow.SendMessageAsync(conversationId, text, format, attachments);
    }

    public Task<List<DecryptedMessage>> FetchMessagesAsync(
        Guid conversationId, long? afterSeq, long? beforeSeq, int limit)
        => _flow.FetchMessagesAsync(conversationId, afterSeq, beforeSeq, limit);

    public Task ModerateMessageAsync(Guid conversationId, long seq, bool hidden, string? reason)
        => _gateway.ModerateMessageAsync(conversationId, seq, hidden, reason);

    public Task<Guid> UploadFileAsync(Guid conversationId, Stream fileStream, string fileName, string mimeType)
        => _files.UploadAsync(conversationId, fileStream, fileName, mimeType, CancellationToken.None);

    public Task<Stream> DownloadFileAsync(Guid conversationId, Guid fileId)
        => _files.DownloadAsync(conversationId, fileId, CancellationToken.None);

    public Task PreReserveSeqsAsync(Guid conversationId, int count)
        => _flow.PreReserveSeqsAsync(conversationId, count);

    public Task ForceFinalizeMigrationAsync(Guid conversationId, Guid jobId)
        => _custodian.ForceFinalizeAsync(conversationId, jobId);

    /// <summary>
    /// Invio file end-to-end cifrato: genera una DEK nuova, cifra il file con essa, wrappa la DEK
    /// con la CEK della conversazione, carica il ciphertext e posta un messaggio
    /// <see cref="MessageType.FileRef"/> col relativo <see cref="AttachmentRef"/>.
    /// </summary>
    public Task SendFileAsync(
        Guid conversationId,
        byte[] fileBytes,
        string fileName,
        string mimeType,
        string? caption = null,
        IProgress<int>? encryptProgress = null)
    {
        GuardAgainstActiveMigration(conversationId);
        // L'invio finale del messaggio passa per SendMessageAsync (che ri-applica il guard).
        return _fileOrchestrator.SendEncryptedFileAsync(
            conversationId, fileBytes, fileName, mimeType, caption,
            (cid, text, atts) => SendMessageAsync(cid, text, atts),
            CancellationToken.None);
    }

    /// <summary>
    /// Scarica il blob cifrato, unwrappa la DEK con la CEK dell'epoch corrispondente e ritorna
    /// i byte del file decifrato.
    /// </summary>
    public Task<byte[]> DownloadAttachmentAsync(
        Guid conversationId, AttachmentRef attachment, int epochId)
        => _fileOrchestrator.DownloadAndDecryptAttachmentAsync(
            conversationId, attachment, epochId, CancellationToken.None);

    public async Task AddMemberAsync(Guid conversationId, Guid userId, bool includeHistory)
    {
        await _gateway.AddMemberAsync(conversationId, userId, includeHistory);
        // E2EE (S1): il server non copia più i wrap; ri-wrappiamo la CEK corrente per i device del
        // nuovo membro. (includeHistory per gli epoch passati è un follow-up del provisioning.)
        await _cekProvisioner.GrantCurrentAsync(conversationId);
    }

    public Task ProvisionConversationKeysAsync(Guid conversationId, int epochId)
        => _cekProvisioner.ProvisionEpochAsync(conversationId, epochId);

    public Task GrantConversationKeysAsync(Guid conversationId)
        => _cekProvisioner.GrantCurrentAsync(conversationId);

    public Task SetMemberRoleAsync(Guid conversationId, Guid userId, string role)
        => _gateway.SetMemberRoleAsync(conversationId, userId, role);

    public Task RenameConversationAsync(Guid conversationId, string newName)
        => _gateway.RenameConversationAsync(conversationId, newName);

    public Task DeleteConversationAsync(Guid conversationId)
        => _gateway.DeleteConversationAsync(conversationId);

    public Task RemoveMemberAsync(
        Guid conversationId,
        Guid userId,
        MigrationMode? migration,
        IProgress<MigrationProgress>? progress = null,
        CancellationToken ct = default)
        => _fileOrchestrator.HandleMemberRemovalAsync(conversationId, userId, migration, progress, ct);
}
