using ECHAT.Client.Core.Interfaces;
using ECHAT.Models.Domain;

namespace ECHAT.Client.Core.Services;

/// <summary>
/// Offloader della crypto di rotazione. Compone l'<see cref="ICryptoEngine"/> async (in produzione
/// backed-by WebCrypto) e <see cref="FileCipher"/> per il re-wrap delle DEK. È l'unica
/// implementazione: in browser le operazioni girano comunque su WebCrypto via l'engine iniettato,
/// nei test sull'engine AES-GCM C#.
/// </summary>
public class InProcessCustodianCryptoOffloader : ICustodianCryptoOffloader
{
    private readonly ICryptoEngine _crypto;
    private readonly FileCipher _files;
    private readonly IDeviceKeyStore _keyStore;

    public InProcessCustodianCryptoOffloader(ICryptoEngine crypto, FileCipher files, IDeviceKeyStore keyStore)
    {
        _crypto = crypto;
        _files = files;
        _keyStore = keyStore;
    }

    public async Task<MessagePayload?> TryDecryptAsync(
        byte[] oldCiphertext,
        byte[] oldNonce,
        byte[] oldCek,
        Guid conversationId,
        Guid messageId,
        long seq,
        int oldEpochId,
        CancellationToken ct)
    {
        try
        {
            return await _crypto.DecryptAsync(
                oldCiphertext, oldNonce, oldCek,
                conversationId, messageId, seq, oldEpochId);
        }
        catch
        {
            return null;
        }
    }

    public async Task<ReencryptedEnvelope> EncryptAndSignAsync(
        MessagePayload payload,
        byte[] oldCek,
        byte[] newCek,
        Guid senderDeviceId,
        Guid conversationId,
        Guid messageId,
        long seq,
        int newEpochId,
        byte[]? overridePrevEnvelopeHash,
        CancellationToken ct)
    {
        var rewrappedAttachments = await RewrapAttachmentDeksAsync(payload.Attachments, oldCek, newCek);

        var hasOverride = overridePrevEnvelopeHash is { Length: > 0 };
        if (hasOverride || rewrappedAttachments is not null)
        {
            payload = new MessagePayload
            {
                Seq = payload.Seq,
                PrevEnvelopeHash = hasOverride ? overridePrevEnvelopeHash! : payload.PrevEnvelopeHash,
                Text = payload.Text,
                Format = payload.Format,
                Attachments = rewrappedAttachments ?? payload.Attachments,
                Mentions = payload.Mentions,
                ReplyTo = payload.ReplyTo,
                Invisible = payload.Invisible
            };
        }

        var encrypted = await _crypto.EncryptAsync(
            payload, newCek,
            conversationId, messageId, seq, newEpochId);

        // Firma ECDSA del custode sul digest del NUOVO envelope (stesso pre-image che il ricevente
        // ricalcola). SenderDeviceId = il device del custode: il messaggio ri-cifrato è la sua
        // attestazione (la firma originale dell'autore non è ricostruibile, cfr. redesign §7).
        var hashEnvelope = new MessageEnvelope
        {
            ConversationId = conversationId,
            MessageId = messageId,
            Seq = seq,
            EpochId = newEpochId,
            SenderDeviceId = senderDeviceId,
            // DEVE includere il Nonce: EnvelopeHasher lo copre, e il ricevente ricalcola il digest
            // sull'envelope finale (che ha questo stesso Nonce). Senza, la firma del custode non verifica.
            Nonce = encrypted.Nonce,
            Ciphertext = encrypted.Ciphertext,
        };
        var signature = await _keyStore.SignHashAsync(EnvelopeHasher.Compute(hashEnvelope));

        return new ReencryptedEnvelope(encrypted.Ciphertext, encrypted.Nonce, signature);
    }

    private async Task<List<AttachmentRef>?> RewrapAttachmentDeksAsync(
        List<AttachmentRef>? attachments, byte[] oldCek, byte[] newCek)
    {
        if (attachments is null || attachments.Count == 0) return null;

        var result = new List<AttachmentRef>(attachments.Count);
        foreach (var att in attachments)
        {
            var dek = await _files.UnwrapKeyAsync(att.WrappedFileDek, oldCek);
            var rewrapped = await _files.WrapKeyAsync(dek, newCek);
            result.Add(new AttachmentRef
            {
                FileId = att.FileId,
                WrappedFileDek = rewrapped,
                FileName = att.FileName,
                MimeType = att.MimeType,
                Size = att.Size
            });
        }
        return result;
    }
}
