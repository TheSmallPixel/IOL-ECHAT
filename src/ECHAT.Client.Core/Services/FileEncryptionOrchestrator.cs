using System.Security.Cryptography;
using ECHAT.Client.Core.Interfaces;
using ECHAT.Models.Domain;
using ECHAT.Models.Enums;

namespace ECHAT.Client.Core.Services;

/// <summary>
/// Logica end-to-end per allegati cifrati e rimozione membri con migrazione, estratta da
/// ChatSdkService. Vive in Client.Core: dipende solo da interfacce/servizi Core
/// (<see cref="MessageFlowOrchestrator"/>, <see cref="IFileTransferManager"/>,
/// <see cref="FileCipher"/>, <see cref="IFileBodyCipher"/>, <see cref="ICustodianWorker"/>,
/// <see cref="IMigrationStateTracker"/>). L'App resta thin: guard contro migrazioni attive,
/// transport binding e l'invio finale del messaggio passati come delegate.
/// </summary>
public class FileEncryptionOrchestrator
{
    private readonly MessageFlowOrchestrator _flow;
    private readonly IFileTransferManager _files;
    private readonly FileCipher _fileCipher;
    private readonly IFileBodyCipher _bodyCipher;
    private readonly ICustodianWorker _custodian;
    private readonly IMigrationStateTracker _migrations;
    private readonly IChatServerGateway _gateway;
    private readonly CekProvisioner _cekProvisioner;

    public FileEncryptionOrchestrator(
        MessageFlowOrchestrator flow,
        IFileTransferManager files,
        FileCipher fileCipher,
        IFileBodyCipher bodyCipher,
        ICustodianWorker custodian,
        IMigrationStateTracker migrations,
        IChatServerGateway gateway,
        CekProvisioner cekProvisioner)
    {
        _flow = flow;
        _files = files;
        _fileCipher = fileCipher;
        _bodyCipher = bodyCipher;
        _custodian = custodian;
        _migrations = migrations;
        _gateway = gateway;
        _cekProvisioner = cekProvisioner;
    }

    /// <summary>
    /// Invio file end-to-end cifrato: genera una DEK nuova, cifra il file con essa, wrappa la DEK
    /// con la CEK della conversazione, carica il ciphertext e invoca <paramref name="sendMessageAsync"/>
    /// per postare il messaggio col relativo <see cref="AttachmentRef"/>. Il delegate consente all'App
    /// di applicare il guard contro migrazioni attive sul send finale.
    /// </summary>
    public async Task SendEncryptedFileAsync(
        Guid conversationId,
        byte[] fileBytes,
        string fileName,
        string mimeType,
        string? caption,
        Func<Guid, string, List<AttachmentRef>, Task> sendMessageAsync,
        CancellationToken ct = default)
    {
        var (cek, _) = await _flow.GetCurrentCekAsync(conversationId);

        // Corpo del file: AES-GCM nel Web Worker (`crypto-worker.js`), hardware via `crypto.subtle`
        // e fuori dal thread UI. Produce il wire format `0xA1 | IV(12) | ciphertext+tag`.
        // Il wrap della DEK resta in C# (32 byte; il JSInterop dominerebbe il costo).
        var dek = RandomNumberGenerator.GetBytes(32);
        var ciphertext = await _bodyCipher.EncryptAsync(fileBytes, dek);
        var wrappedDek = await _fileCipher.WrapKeyAsync(dek, cek);

        // Upload del ciphertext via l'uploader a chunk (PUT in parallelo).
        using var ms = new MemoryStream(ciphertext);
        var fileId = await _files.UploadAsync(conversationId, ms, fileName, mimeType, ct);

        var attachment = new AttachmentRef
        {
            FileId = fileId,
            WrappedFileDek = wrappedDek,
            FileName = fileName,
            MimeType = mimeType,
            Size = fileBytes.LongLength
        };

        await sendMessageAsync(conversationId, caption ?? string.Empty, new List<AttachmentRef> { attachment });
    }

    /// <summary>
    /// Scarica il blob cifrato, unwrappa la DEK con la CEK dell'epoch corrispondente e ritorna
    /// i byte del file decifrato.
    /// </summary>
    public async Task<byte[]> DownloadAndDecryptAttachmentAsync(
        Guid conversationId, AttachmentRef attachment, int epochId, CancellationToken ct = default)
    {
        var cek = await _flow.GetCekForEpochAsync(conversationId, epochId)
            ?? throw new InvalidOperationException($"No CEK for epoch {epochId}");

        await using var stream = await _files.DownloadAsync(conversationId, attachment.FileId, ct);
        using var ms = new MemoryStream();
        await stream.CopyToAsync(ms, ct);
        var ciphertext = ms.ToArray();

        // Tutti i corpi file sono AES-GCM v1 (magic byte 0xA1, cifrato dal Web Worker). Se manca
        // il magic, il buffer è corrotto o anteriore al formato corrente: meglio fallire subito.
        if (ciphertext.Length == 0 || ciphertext[0] != _bodyCipher.AesGcmV1Magic)
            throw new CryptographicException(
                "Unsupported file format: expected AES-GCM v1 (magic 0xA1). " +
                "Legacy HMAC-CTR file decoding has been removed.");

        var dek = await _fileCipher.UnwrapKeyAsync(attachment.WrappedFileDek, cek);
        return await _bodyCipher.DecryptAsync(ciphertext, dek);
    }

    /// <summary>
    /// Rimuove un membro e, in base a <paramref name="migration"/>, avvia la strategia di migrazione:
    /// null/RewrapOnly = rotazione epoch + nuova CEK per i membri rimasti (il server shredda anche i
    /// wrap del rimosso dentro RemoveMember; nessun job server-side);
    /// FullReencrypt = il custode (questo device) pilota decrypt/re-encrypt di tutti gli envelope
    /// storici, marcando la conversazione come "in migrazione" così SendMessage/SendFile/AiAutoReply
    /// tirano un'eccezione finché non completa.
    /// </summary>
    public async Task HandleMemberRemovalAsync(
        Guid conversationId,
        Guid userId,
        MigrationMode? migration,
        IProgress<MigrationProgress>? progress = null,
        CancellationToken ct = default)
    {
        // Il server rimuove il membro e bumpa l'epoch, ritornando il valore AUTORITATIVO (race-free:
        // non assumiamo oldEpoch+1 lato client).
        var newEpoch = await _gateway.RemoveMemberAsync(conversationId, userId);

        // E2EE (S1): il server NON genera più la CEK del nuovo epoch. La generiamo qui e la wrappiamo
        // SOLO per i device dei membri rimasti (la directory esclude già l'utente appena rimosso) 
        // il rimosso non ha la chiave dell'epoch nuovo (forward secrecy). Va fatto PRIMA del custode,
        // che in FullReencrypt ri-cifra usando proprio la CEK del nuovo epoch.
        await _cekProvisioner.ProvisionEpochAsync(conversationId, newEpoch);

        switch (migration)
        {
            case null:
            case MigrationMode.RewrapOnly:
                // Tutto il lavoro RewrapOnly è già successo: epoch bump + shred dei wrap del
                // rimosso in RemoveMember (server), nuova CEK per i membri rimasti qui sopra.
                // Nessun job server-side: StartMigration è riservato a FullReencrypt.
                return;

            case MigrationMode.FullReencrypt:
                // Lo scope di BeginLocal marca la conversazione come "in migrazione". Inoltriamo gli
                // update sia al caller (progress) sia al tracker (per gli altri osservatori).
                using (_migrations.BeginLocal(conversationId))
                {
                    // NB: NON usare Progress<T> qui. Progress<T>.Report marshalla via
                    // SynchronizationContext.Post (thread pool in headless/test), che può riordinare
                    // report ravvicinati: il tracker vedrebbe Completed prima di Reencrypting. Il
                    // custode emette le fasi in ordine su un singolo flusso, quindi forwardiamo in
                    // modo sincrono per preservarlo.
                    var combined = new SyncProgress<MigrationProgress>(p =>
                    {
                        _migrations.Update(conversationId, p);
                        progress?.Report(p);
                    });
                    await _custodian.RunStrongRevokeAsync(
                        conversationId, MigrationMode.FullReencrypt, ct, combined);
                }
                return;

            default:
                throw new ArgumentOutOfRangeException(nameof(migration), migration, "Unknown migration mode");
        }
    }

    /// <summary>
    /// <see cref="IProgress{T}"/> sincrono: invoca il callback inline sul thread del chiamante,
    /// preservando l'ordine dei report. Diversamente da <see cref="Progress{T}"/> non marshalla
    /// via <see cref="SynchronizationContext"/>, quindi report ravvicinati non si riordinano.
    /// </summary>
    private sealed class SyncProgress<T> : IProgress<T>
    {
        private readonly Action<T> _handler;
        public SyncProgress(Action<T> handler) => _handler = handler;
        public void Report(T value) => _handler(value);
    }
}
