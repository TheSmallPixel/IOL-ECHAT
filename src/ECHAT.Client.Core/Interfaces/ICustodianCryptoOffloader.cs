using ECHAT.Models.Domain;

namespace ECHAT.Client.Core.Interfaces;

/// <summary>
/// Crypto offload per FullReencrypt. Le due operazioni sono splittate così CustodianWorker
/// può costruire una pipeline a 3 stage:
///   1) decrypt in parallelo (operazioni indipendenti tra envelope)
///   2) encrypt+sign in seq order (la catena di hash impone la serializzazione)
///   3) POST in parallelo (chiavi seq indipendenti server-side)
///
/// Astratto così l'orchestratore non sa se la crypto gira sul thread principale o dentro un
/// pool di Web Worker. L'implementazione Web Worker vive in Client.App (ha bisogno di
/// IJSRuntime), mentre il default in-process sta in Client.Core e va bene per test e fallback.
///
/// THREAD-SAFETY: gli implementatori devono supportare chiamate concorrenti.
/// </summary>
public interface ICustodianCryptoOffloader
{
    /// <summary>
    /// Decifra l'envelope con la vecchia CEK. Ritorna <c>null</c> se non riesce (tombstone con
    /// ciphertext vuoto, payload scritto da un device sconosciuto, ...): CustodianWorker tratta
    /// gli skip come "non ricifrare, lascia l'envelope com'è".
    /// </summary>
    Task<MessagePayload?> TryDecryptAsync(
        byte[] oldCiphertext,
        byte[] oldNonce,
        byte[] oldCek,
        Guid conversationId,
        Guid messageId,
        long seq,
        int oldEpochId,
        CancellationToken ct);

    /// <summary>
    /// Cifra il payload con la nuova CEK e firma il ciphertext con la chiave privata del custode.
    /// Se il payload contiene allegati, le rispettive <c>WrappedFileDek</c> vengono unwrappate
    /// con <paramref name="oldCek"/> e ri-wrappate con <paramref name="newCek"/>: senza questo
    /// passo i file diventerebbero illeggibili dopo la rotazione (UnwrapKey fallirebbe come MAC).
    ///
    /// <paramref name="overridePrevEnvelopeHash"/>: se non null, sostituisce
    /// <c>PrevEnvelopeHash</c> nel payload prima di cifrare. CustodianWorker lo passa per
    /// ricostruire la catena tra envelope appena ri-cifrati (re-encrypt cambia EpochId+Ciphertext
    /// e quindi l'hash, vedi <see cref="EnvelopeHasher"/>).
    /// </summary>
    /// <para><paramref name="senderDeviceId"/> è il device che firma (e che verrà stampato come
    /// SenderDeviceId sull'envelope ri-cifrato): il custode firma la propria attestazione con la
    /// chiave ECDSA del proprio device sul digest di <see cref="EnvelopeHasher"/> del nuovo envelope.</para>
    Task<ReencryptedEnvelope> EncryptAndSignAsync(
        MessagePayload payload,
        byte[] oldCek,
        byte[] newCek,
        Guid senderDeviceId,
        Guid conversationId,
        Guid messageId,
        long seq,
        int newEpochId,
        byte[]? overridePrevEnvelopeHash,
        CancellationToken ct);
}

public record ReencryptedEnvelope(byte[] Ciphertext, byte[] Nonce, byte[] Signature);
