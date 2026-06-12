using System.Collections.Concurrent;
using ECHAT.Client.Core.Interfaces;
using ECHAT.Models.Domain;
using ECHAT.Models.Dtos;
using ECHAT.Models.Enums;

namespace ECHAT.Client.Core.Services;

/// <summary>
/// Orchestratore del flusso dei messaggi: gestisce la cache CEK in memoria, costruisce gli envelope
/// cifrati, valida la catena al fetch. Tutta la I/O passa da <see cref="IChatServerGateway"/>;
/// la crittografia da <see cref="ICryptoEngine"/>; la firma/verifica chain da
/// <see cref="IChainValidator"/>. Niente HTTP, niente Blazor.
/// </summary>
public class MessageFlowOrchestrator
{
    private readonly IChatServerGateway _gateway;
    private readonly ICryptoEngine _crypto;
    private readonly IDeviceKeyStore _keyStore;
    private readonly ISequenceLeaseManager _leaseManager;
    private readonly IChainValidator _chainValidator;

    // Cache CEK in memoria, chiave (conversationId, epochId).
    // La CEK "corrente" per una conversazione è quella con l'epoch più alto visto finora.
    private readonly Dictionary<(Guid conversationId, int epochId), byte[]> _cekCache = new();
    private readonly Dictionary<Guid, int> _currentEpoch = new();

    // Serializzazione di SendMessageAsync per conversazione.
    // Senza questo lock, send concorrenti dallo stesso client (es. tre upload di file che
    // completano vicini + un testo battuto a mano) leggerebbero la STESSA "ultima envelope"
    // da GetLatestEnvelopeAsync e calcolerebbero lo stesso PrevEnvelopeHash. Quando il server
    // assegna seq diversi, la catena si rompe: il successivo punta allo stesso predecessore
    // del precedente, non al precedente in seq order. Risultato visibile lato lettore:
    // "Chain: Break" che varia da viewer a viewer perché ciascuno fa diff con il proprio
    // ordine di display. Il lock garantisce che ogni send veda gli envelope precedentemente
    // postati da QUESTO client prima di calcolare il proprio prev hash.
    //
    // Nota: questo NON risolve la race tra client diversi (es. due admin che inviano in
    // contemporanea da device diversi); per quella servirebbe serializzazione server-side
    // per conversazione. Risolve invece il caso comune "spam di file + testo da un client".
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _sendLocks = new();

    public MessageFlowOrchestrator(
        IChatServerGateway gateway,
        ICryptoEngine crypto,
        IDeviceKeyStore keyStore,
        ISequenceLeaseManager leaseManager,
        IChainValidator chainValidator)
    {
        _gateway = gateway;
        _crypto = crypto;
        _keyStore = keyStore;
        _leaseManager = leaseManager;
        _chainValidator = chainValidator;
    }

    public void SetCek(Guid conversationId, int epochId, byte[] cek)
    {
        _cekCache[(conversationId, epochId)] = cek;
        if (!_currentEpoch.TryGetValue(conversationId, out var current) || epochId > current)
            _currentEpoch[conversationId] = epochId;
    }

    /// <summary>
    /// Restituisce la CEK + epoch che il client deve usare per i *nuovi* messaggi.
    /// Sceglie il wrap con l'epoch più alto restituito dal server per il device corrente.
    /// </summary>
    public async Task<(byte[] cek, int epochId)> GetCurrentCekAsync(Guid conversationId)
    {
        if (_currentEpoch.TryGetValue(conversationId, out var epoch)
            && _cekCache.TryGetValue((conversationId, epoch), out var cached))
            return (cached, epoch);

        var wraps = await _gateway.GetKeysAsync(conversationId);
        var wrap = wraps.OrderByDescending(w => w.EpochId).FirstOrDefault();
        if (wrap is null || wrap.WrappedCek.Length == 0)
            throw new InvalidOperationException($"No CEK available for conversation {conversationId}");

        // E2EE: la CEK arriva wrappata con la nostra RSA pubblica (0xB2). La unwrappiamo con la
        // chiave privata del device (mai esposta in chiaro). La CEK grezza vive solo in memoria.
        var cek = await _keyStore.UnwrapCekAsync(wrap.WrappedCek);
        _cekCache[(conversationId, wrap.EpochId)] = cek;
        _currentEpoch[conversationId] = wrap.EpochId;
        return (cek, wrap.EpochId);
    }

    /// <summary>
    /// Restituisce la CEK attiva all'<paramref name="epochId"/> indicato, scaricandola se serve.
    /// Necessaria per decifrare i messaggi storici dopo una rotazione di epoch.
    /// </summary>
    public async Task<byte[]?> GetCekForEpochAsync(Guid conversationId, int epochId)
    {
        if (_cekCache.TryGetValue((conversationId, epochId), out var cached))
            return cached;

        var wraps = await _gateway.GetKeysAsync(conversationId, epochId);
        var wrap = wraps.FirstOrDefault();
        if (wrap is null || wrap.WrappedCek.Length == 0) return null;

        var cek = await _keyStore.UnwrapCekAsync(wrap.WrappedCek);
        _cekCache[(conversationId, epochId)] = cek;
        return cek;
    }

    /// <summary>
    /// Riserva in anticipo <paramref name="count"/> seq dal server in una sola chiamata HTTP.
    /// Utile quando il chiamante sa di dover spedire un batch (es. upload multi-file): senza
    /// pre-reservation ogni SendMessageAsync farebbe una HTTP roundtrip per ReserveSeqAsync,
    /// con pre-reservation paghiamo un solo round-trip per tutto il batch e i SendMessage
    /// successivi consumano dal lease in memoria.
    ///
    /// I seq pre-riservati restano validi fino alla scadenza del lease (server-side TTL).
    /// Quelli non consumati diventano gap nel log della conversazione; il meccanismo dei
    /// tombstone lato server li riempirà quando rilevante.
    /// </summary>
    public async Task PreReserveSeqsAsync(Guid conversationId, int count)
    {
        if (count <= 0) return;
        // Stessa sem dei send: così la pre-reservation non si interleaja con un send in volo.
        var sem = _sendLocks.GetOrAdd(conversationId, _ => new SemaphoreSlim(1, 1));
        await sem.WaitAsync();
        try
        {
            var reservation = await _gateway.ReserveSeqAsync(conversationId, count);
            _leaseManager.ApplyReservation(conversationId, reservation);
        }
        finally
        {
            sem.Release();
        }
    }

    public async Task SendMessageAsync(
        Guid conversationId,
        string text,
        MessageFormat format,
        List<AttachmentRef>? attachments = null)
    {
        // Serializza per conversazione: vedi commento su _sendLocks.
        var sem = _sendLocks.GetOrAdd(conversationId, _ => new SemaphoreSlim(1, 1));
        await sem.WaitAsync();
        try
        {
            // 1. Riserva 1 seq per send. Le reservation a batch (count > 1) creavano un
            // problema di UX visibile soprattutto con l'AI auto-reply: ogni client conserva
            // localmente seq pre-allocati e li consuma nell'ordine in cui *lui* invia, ma
            // diversi client hanno range diversi. Risultato: l'AI che gira sulla sessione di
            // TheSmallPixel pesca un seq basso pre-allocato a lei e la sua risposta finisce
            // numericamente PRIMA del messaggio di Giorgio a cui sta rispondendo. Con count=1
            // ogni reservation chiede al server *adesso* il prossimo seq libero, quindi i seq
            // riflettono l'ordine cronologico delle reservation. Costo: +1 HTTP per messaggio
            // (~50ms). NB: non risolve la race chain tra client diversi sul GetLatestEnvelope;
            // quella richiederebbe un'allocazione atomica server-side di seq+prevHash.
            if (!_leaseManager.HasAvailableSeq(conversationId))
            {
                var reservation = await _gateway.ReserveSeqAsync(conversationId, count: 1);
                _leaseManager.ApplyReservation(conversationId, reservation);
            }
            var seq = await _leaseManager.GetNextSeqAsync(conversationId);

            // 2. Hash dell'ultimo envelope (chain globale). Eseguito DOPO il lock così i send
            // concorrenti dallo stesso client vedono il nostro envelope appena postato prima
            // di calcolare il loro prev hash.
            var latest = await _gateway.GetLatestEnvelopeAsync(conversationId);
            var prevHash = latest is null
                ? Array.Empty<byte>()
                : _crypto.ComputeEnvelopeHash(latest);

            // 3. Costruisce il payload
            var payload = new MessagePayload
            {
                Seq = seq,
                PrevEnvelopeHash = prevHash,
                Text = text,
                Format = format,
                Attachments = attachments,
                Invisible = false
            };

            // 4. Cifra con la CEK dell'epoch corrente
            var (cek, epochId) = await GetCurrentCekAsync(conversationId);
            var messageId = Guid.NewGuid();
            var encrypted = await _crypto.EncryptAsync(payload, cek, conversationId, messageId, seq, epochId);

            // 5. Costruisce l'envelope e lo FIRMA con la ECDSA del device (S3/S4). La firma è sul
            // digest di EnvelopeHasher, che lega conv/msg/seq/epoch/senderDeviceId + ciphertext.
            // SenderUserId/SenderDeviceId sono l'identità reale che il server valida contro il JWT.
            var deviceId = await _keyStore.GetDeviceIdAsync();
            var senderUserId = await _gateway.GetCurrentUserIdAsync();
            var leaseToken = _leaseManager.GetLeaseToken(conversationId);
            var type = (attachments?.Count ?? 0) > 0 ? MessageType.FileRef : MessageType.Text;

            // EnvelopeHasher non include la firma: costruiamo l'envelope "non firmato", calcoliamo il
            // digest, firmiamo, e ricostruiamo l'envelope finale con la firma (i campi sono init-only).
            var unsigned = new MessageEnvelope
            {
                ConversationId = conversationId,
                MessageId = messageId,
                Seq = seq,
                EpochId = epochId,
                SenderDeviceId = deviceId,
                SenderUserId = senderUserId,
                Nonce = encrypted.Nonce,
                Ciphertext = encrypted.Ciphertext,
                Signature = Array.Empty<byte>(),
                LeaseToken = leaseToken,
                Type = type
            };
            var signature = await _keyStore.SignHashAsync(EnvelopeHasher.Compute(unsigned));
            var envelope = new MessageEnvelope
            {
                ConversationId = conversationId,
                MessageId = messageId,
                Seq = seq,
                EpochId = epochId,
                SenderDeviceId = deviceId,
                SenderUserId = senderUserId,
                Nonce = encrypted.Nonce,
                Ciphertext = encrypted.Ciphertext,
                Signature = signature,
                LeaseToken = leaseToken,
                Type = type
            };

            await _gateway.PostMessageAsync(envelope);
        }
        finally
        {
            sem.Release();
        }
    }

    public async Task<List<DecryptedMessage>> FetchMessagesAsync(
        Guid conversationId, long? afterSeq, long? beforeSeq, int limit)
    {
        // Boundary di chain in parallelo con gli envelope: zero overhead aggiuntivo di latenza,
        // un solo round-trip extra (di solito ritorna una lista vuota).
        var envelopesTask = _gateway.FetchEnvelopesAsync(conversationId, afterSeq, beforeSeq, limit);
        var boundariesTask = _gateway.GetChainBoundariesAsync(conversationId);
        // Directory dei device per verificare le firme: deviceId  chiave pubblica ECDSA (SPKI).
        var devicesTask = _gateway.GetConversationDevicesAsync(conversationId);
        await Task.WhenAll(envelopesTask, boundariesTask, devicesTask);
        var envelopes = await envelopesTask;
        var boundaries = await boundariesTask;
        // deviceId  chiave pubblica ECDSA. Pre-popolata coi device dei membri ATTIVI; i mittenti non
        // più attivi (es. rimossi) vengono risolti lazy via GetDeviceAsync: la rimozione del membro non
        // revoca il device, quindi i loro messaggi storici restano verificabili. Cache anche dei null
        // (device sconosciuto) per non rifetchare.
        var ecdsaByDevice = new Dictionary<Guid, byte[]?>();
        foreach (var d in await devicesTask)
            ecdsaByDevice[d.DeviceId] = d.EcdsaSpki;

        async Task<byte[]?> ResolveSenderSpkiAsync(Guid senderDeviceId)
        {
            if (ecdsaByDevice.TryGetValue(senderDeviceId, out var cached)) return cached;
            var dev = await _gateway.GetConversationSenderDeviceAsync(conversationId, senderDeviceId);
            var spki = dev?.EcdsaSpki;
            ecdsaByDevice[senderDeviceId] = spki;
            return spki;
        }

        // Pre-carica la cache con la CEK dell'epoch corrente così SendMessage non rifarà il fetch.
        await GetCurrentCekAsync(conversationId);

        var result = new List<DecryptedMessage>();

        // Chain globale: il prevEnvelopeHash di ogni messaggio punta al precedente (qualunque sender),
        // perché i sender scaricano l'hash più recente prima di inviare.
        var prevHash = Array.Empty<byte>();

        // Iterator sui boundary ordinati per AfterSeq. Quando incontriamo il primo envelope con
        // seq > boundary.AfterSeq, resettiamo prevHash a vuoto (chain restart legittimo dopo
        // FullReencrypt) ed avanziamo al boundary successivo. Così il validator non urla
        // "Chain: Break" sull'envelope subito dopo il punto di riscrittura.
        var boundaryQueue = new Queue<ChainBoundary>(boundaries.OrderBy(b => b.AfterSeq));

        foreach (var env in envelopes)
        {
            while (boundaryQueue.TryPeek(out var nextBoundary) && env.Seq > nextBoundary.AfterSeq)
            {
                prevHash = Array.Empty<byte>();
                boundaryQueue.Dequeue();
            }
            try
            {
                // 1. CEK dell'epoch dell'envelope (lo storico può usare CEK vecchie)
                var cek = await GetCekForEpochAsync(conversationId, env.EpochId)
                    ?? throw new InvalidOperationException($"No CEK for epoch {env.EpochId}");

                // 2. Decifra (verifica anche il MAC: se manomesso, eccezione)
                var payload = await _crypto.DecryptAsync(env.Ciphertext, env.Nonce, cek,
                    env.ConversationId, env.MessageId, env.Seq, env.EpochId);

                // 3. Validazione: seq + chain + firma ECDSA del mittente + esito decifratura (qui true:
                // se DecryptAsync sopra non ha lanciato, il MAC AES-GCM ha tenuto).
                var senderSpki = await ResolveSenderSpkiAsync(env.SenderDeviceId);
                var chainResult = await _chainValidator.ValidateAsync(env, payload, prevHash, senderSpki, decryptionSucceeded: true);
                prevHash = chainResult.CurrentEnvelopeHash;

                // Moderazione: la validazione (chain/firma) gira sul payload reale, ma se il messaggio
                // è nascosto NON propaghiamo il contenuto al modello UI: sostituiamo un payload vuoto
                // così il testo decifrato non finisce nel bubble (la UI mostra un placeholder).
                var displayPayload = env.IsHidden
                    ? new MessagePayload { Seq = env.Seq, Text = string.Empty }
                    : payload;

                result.Add(new DecryptedMessage
                {
                    MessageId = env.MessageId,
                    Seq = env.Seq,
                    EpochId = env.EpochId,
                    Type = env.Type,
                    Payload = displayPayload,
                    SenderDeviceId = env.SenderDeviceId,
                    SenderUserId = env.SenderUserId,
                    IsVerified = chainResult.IsValid,
                    SeqValid = chainResult.SeqValid,
                    ChainValid = chainResult.ChainValid,
                    DecryptionValid = chainResult.DecryptionValid,
                    MacValid = chainResult.MacValid,
                    Invisible = payload.Invisible,
                    CreatedAt = env.CreatedAt,
                    IsHidden = env.IsHidden,
                    ModeratedByUserId = env.ModeratedByUserId,
                    ModerationReason = env.ModerationReason
                });
            }
            catch
            {
                result.Add(new DecryptedMessage
                {
                    MessageId = env.MessageId,
                    Seq = env.Seq,
                    EpochId = env.EpochId,
                    Type = env.Type,
                    Payload = new MessagePayload { Seq = env.Seq, Text = "[Decryption failed]" },
                    SenderDeviceId = env.SenderDeviceId,
                    SenderUserId = env.SenderUserId,
                    IsVerified = false,
                    SeqValid = false,
                    ChainValid = false,
                    DecryptionValid = false,
                    MacValid = false,
                    Invisible = false,
                    CreatedAt = env.CreatedAt,
                    IsHidden = env.IsHidden,
                    ModeratedByUserId = env.ModeratedByUserId,
                    ModerationReason = env.ModerationReason
                });
            }
        }

        return result;
    }
}
