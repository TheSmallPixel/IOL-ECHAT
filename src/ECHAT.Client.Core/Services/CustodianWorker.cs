using System.Collections.Concurrent;
using ECHAT.Client.Core.Interfaces;
using ECHAT.Models.Domain;
using ECHAT.Models.Enums;

namespace ECHAT.Client.Core.Services;

/// <summary>
/// Worker client-side (solo admin) per i passaggi delle saga che il server non può fare da solo:
/// concessione della history a nuovi membri, tombstone per i gap e migrazione FullReencrypt
/// (decifra con la vecchia CEK, ricifra con la nuova, POST della sostituzione).
/// </summary>
public class CustodianWorker : ICustodianWorker
{
    private const int FullReencryptBatchSize = 50;

    // Concorrenza per Stage 1 (decrypt) e Stage 3 (POST). Stage 2 (encrypt+sign) resta
    // serializzato dalla catena di hash. Tenuto basso per non saturare memoria del browser
    // (pool di Web Worker) né generare burst HTTP eccessivi.
    private const int FullReencryptParallelism = 3;

    private readonly IChatServerGateway _gateway;
    private readonly ICustodianCryptoOffloader _crypto;
    private readonly IDeviceKeyStore _keyStore;

    public CustodianWorker(
        IChatServerGateway gateway,
        ICustodianCryptoOffloader crypto,
        IDeviceKeyStore keyStore)
    {
        _gateway = gateway;
        _crypto = crypto;
        _keyStore = keyStore;
    }

    public Task RewrapKeysForMemberAsync(Guid conversationId, Guid newMemberDeviceId)
        => _gateway.AddMemberAsync(conversationId, newMemberDeviceId, includeHistory: true);

    public Task ForceFinalizeAsync(Guid conversationId, Guid jobId)
        => _gateway.ForceFinalizeMigrationAsync(conversationId, jobId);

    /// <summary>
    /// L'epoch target del FullReencrypt: il massimo tra le epoch per cui il custode ha una
    /// wrap. Idealmente coincide con conversation.CurrentEpochId; se non coincide significa
    /// che il custode non ha la CEK per il nuovo epoch (situazione anomala: RotateEpochAsync
    /// dovrebbe averla creata al membro che ha rimosso).
    /// </summary>
    private async Task<int> GetTargetEpochAsync(Guid conversationId)
    {
        var keys = await _gateway.GetKeysAsync(conversationId);
        return keys.Select(k => k.EpochId).DefaultIfEmpty(0).Max();
    }

    public async Task GenerateGapTombstonesAsync(Guid conversationId, long fromSeq, long toSeq)
    {
        if (toSeq < fromSeq) throw new ArgumentException($"toSeq {toSeq} < fromSeq {fromSeq}");

        var tombstones = Enumerable.Range(0, (int)(toSeq - fromSeq + 1))
            .Select(i => new TombstoneRecord(Guid.NewGuid(), fromSeq + i, 1))
            .ToList();

        await _gateway.PostTombstonesAsync(conversationId, tombstones);
    }

    public async Task RunStrongRevokeAsync(
        Guid conversationId,
        MigrationMode mode,
        CancellationToken ct,
        IProgress<MigrationProgress>? progress = null)
    {
        // Solo FullReencrypt ha una saga server-side (il server rifiuta gli altri mode):
        // RewrapOnly si esaurisce in RemoveMember + CekProvisioner, senza job né custode.
        if (mode != MigrationMode.FullReencrypt)
            throw new ArgumentException(
                $"RunStrongRevoke supports only FullReencrypt; '{mode}' has no server-side saga.", nameof(mode));

        progress?.Report(new MigrationProgress(MigrationPhase.Starting));

        // jobId fuori dal try così il ramo Cancel sa quale job notificare al server.
        var jobId = Guid.Empty;
        try
        {
            jobId = await _gateway.StartMigrationAsync(conversationId, mode);

            if (mode == MigrationMode.FullReencrypt)
                await DriveFullReencryptAsync(conversationId, jobId, ct, progress);

            // Pre-check lato client: se il server vede ancora envelope al vecchio epoch
            // (perché il custode non aveva la CEK per quegli epoch), fail fast con un
            // errore chiaro PRIMA di chiamare il server finalize che fallirebbe lo stesso
            // ma con una shape diversa. Così la UI può offrire "Force finalize" senza
            // dover parse-are messaggi di errore.
            if (mode == MigrationMode.FullReencrypt)
            {
                int? remaining = null;
                try
                {
                    var newEpoch = await GetTargetEpochAsync(conversationId);
                    remaining = await _gateway.CountEnvelopesBelowEpochAsync(conversationId, newEpoch);
                }
                catch { /* se il check fallisce procediamo: il server farà il vero check */ }

                if (remaining is > 0)
                {
                    throw new MigrationIncompleteException(
                        jobId,
                        remaining.Value,
                        $"{remaining.Value} message(s) could not be re-encrypted because you don't have keys for those older epochs. " +
                        "Have the conversation owner run the migration, or force-finalize to accept the data loss.");
                }
            }

            progress?.Report(new MigrationProgress(MigrationPhase.Finalizing));
            await _gateway.FinalizeMigrationAsync(conversationId, jobId);
            progress?.Report(new MigrationProgress(MigrationPhase.Completed));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Best-effort: notifichiamo il server così emette JobProgress(Cancelled) alle altre
            // tab/admin che osservano via SignalR. Se il server non risponde, l'errore di cancel
            // non deve mascherare l'OperationCanceled originale.
            if (jobId != Guid.Empty)
            {
                try { await _gateway.CancelMigrationAsync(conversationId, jobId); }
                catch { /* swallow: il job potrebbe essere già stato chiuso da un altro percorso */ }
            }
            progress?.Report(new MigrationProgress(MigrationPhase.Cancelled));
            throw;
        }
        catch (Exception ex)
        {
            // Saga fallita (es. Finalize rifiutato dal safety check perché restano envelope al
            // vecchio epoch). Roll back lato server: senza questo cancel il job resta InProgress
            // e HasActiveJobAsync impedirebbe nuovi tentativi di migrazione finché qualcuno non
            // pulisce manualmente il DB. Best-effort: se il cancel fallisce a sua volta, non
            // mascherare l'errore originale.
            if (jobId != Guid.Empty)
            {
                try { await _gateway.CancelMigrationAsync(conversationId, jobId); }
                catch { /* server già down o job già chiuso: l'errore originale è più utile */ }
            }
            progress?.Report(new MigrationProgress(MigrationPhase.Failed, Error: ex.Message));
            throw;
        }
    }

    /// <summary>
    /// FullReencrypt: scorre gli envelope dei vecchi epoch, li decifra con la CEK corrispondente,
    /// li ricifra con la CEK dell'epoch corrente e fa POST della sostituzione.
    /// </summary>
    private async Task DriveFullReencryptAsync(
        Guid conversationId, Guid jobId, CancellationToken ct, IProgress<MigrationProgress>? progress = null)
    {
        var keys = await _gateway.GetKeysAsync(conversationId);
        // E2EE: ogni CEK è wrappata con la RSA del device. La unwrappiamo per poter decifrare/ricifrare.
        var keysByEpoch = new Dictionary<int, byte[]>();
        foreach (var k in keys)
            keysByEpoch[k.EpochId] = await _keyStore.UnwrapCekAsync(k.WrappedCek);
        if (keysByEpoch.Count == 0)
            throw new InvalidOperationException("Could not load CEKs.");

        var newEpoch = keysByEpoch.Keys.Max();
        if (!keysByEpoch.TryGetValue(newEpoch, out var newCek))
            throw new InvalidOperationException("Custodian has no CEK for the current epoch.");

        // Diagnostico: spesso la saga "salta" envelope perché il custode non ha la CEK per
        // l'epoch di quegli envelope (es. utente entrato dopo, senza includeHistory). Tracciamo
        // su console le epoch effettivamente conosciute + l'epoch target scelto così l'utente
        // può confrontare con quello che il server si aspetta (currentEpoch della conversation).
        // Visibile in browser DevTools console.
        Console.WriteLine(
            $"[custodian] FullReencrypt START: conv={conversationId} jobId={jobId} " +
            $"knownEpochs=[{string.Join(",", keysByEpoch.Keys.OrderBy(e => e))}] newEpoch={newEpoch}");

        // Totale envelope da ri-cifrare (epoch < newEpoch). La UI lo usa per la percentuale.
        // Se la chiamata fallisce non blocchiamo: il progress mostrerà solo il contatore.
        int? total;
        try
        {
            total = await _gateway.CountEnvelopesBelowEpochAsync(conversationId, newEpoch);
        }
        catch
        {
            total = null;
        }

        Console.WriteLine(
            $"[custodian] Server reports {total?.ToString() ?? "?"} envelope(s) at epoch < {newEpoch} to rewrite");

        progress?.Report(new MigrationProgress(MigrationPhase.Reencrypting, 0, total, 0));

        // CRITICAL: parte da 0 (non null!). FetchEnvelopesAsync con afterSeq=null restituisce
        // gli ULTIMI N messaggi (semantica "live tail" del MessageRepository), non i PRIMI.
        // Se partissimo con null, la prima iterazione prenderebbe gli ultimi 50, cursor
        // diventerebbe il max seq, la seconda iterazione (afterSeq=max) ritornerebbe vuoto e
        // il loop terminerebbe avendo riscritto SOLO gli ultimi 50 envelope, lasciando tutti
        // gli altri silenziosamente al vecchio epoch. Bug visibile come "X envelope at epoch <
        // current" al finalize, con X = total - 50.
        long? cursor = 0;
        var processed = 0;
        var skipped = 0;
        var batchId = 0;

        // Hash da iniettare come PrevEnvelopeHash nel payload del prossimo envelope ri-cifrato.
        // null = "non sovrascrivere": il payload mantiene il suo PrevEnvelopeHash originale.
        // Lo lasciamo null per il primo envelope (il suo prev punta a qualcosa fuori dal range
        // ri-cifrato, che non è cambiato) e dopo ogni envelope SALTATO (idem: l'hash dell'envelope
        // saltato è invariato, quindi il successivo che linka a lui è già coerente).
        byte[]? prevChainHashOverride = null;

        var parallelOpts = new ParallelOptions
        {
            MaxDegreeOfParallelism = FullReencryptParallelism,
            CancellationToken = ct
        };

        while (true)
        {
            ct.ThrowIfCancellationRequested();

            var batch = await _gateway.FetchEnvelopesAsync(
                conversationId, afterSeq: cursor, beforeSeq: null, limit: FullReencryptBatchSize);
            if (batch.Count == 0) break;

            var signerDeviceId = await _keyStore.GetDeviceIdAsync();
            var signerUserId = await _gateway.GetCurrentUserIdAsync();

            // ───── STAGE 1: DECRYPT IN PARALLELO ─────
            // Ogni envelope si decifra indipendentemente. Con WebWorkerCustodianCryptoOffloader
            // significa fino a FullReencryptParallelism worker che decifrano simultaneamente su
            // thread del browser separati. Quelli che falliscono (tombstone, payload sconosciuto)
            // restano fuori dal dizionario e verranno saltati nelle stage successive.
            // Conteggiamo gli skip lato custode (CEK mancante OR decrypt fallito): questi
            // envelope restano al vecchio epoch e bloccheranno il finalize via safety check
            // server-side (MigrationOrchestratorService.FinalizeAsync). La UI mostra il conteggio
            // così l'utente vede subito che la migrazione è parziale.
            var decrypted = new ConcurrentDictionary<long, MessagePayload>();
            var batchSkipped = 0;
            var skipNoKey = 0;
            var skipDecryptFail = 0;
            await Parallel.ForEachAsync(batch, parallelOpts, async (env, ict) =>
            {
                if (env.EpochId >= newEpoch) return;  // già al nuovo epoch, non è "skipped"
                if (!keysByEpoch.TryGetValue(env.EpochId, out var oldCek))
                {
                    Interlocked.Increment(ref batchSkipped);
                    Interlocked.Increment(ref skipNoKey);
                    return;
                }

                var payload = await _crypto.TryDecryptAsync(
                    env.Ciphertext, env.Nonce, oldCek,
                    env.ConversationId, env.MessageId, env.Seq, env.EpochId, ict);
                if (payload is not null) decrypted[env.Seq] = payload;
                else
                {
                    Interlocked.Increment(ref batchSkipped);
                    Interlocked.Increment(ref skipDecryptFail);
                }
            });
            skipped += batchSkipped;

            if (batchSkipped > 0)
            {
                // Diagnostico: dimostra perché alcuni envelope vengono saltati. Senza questo
                // log la saga finisce silenziosamente con un finalize rifiutato dal server e
                // l'utente non ha modo di sapere perché.
                Console.WriteLine(
                    $"[custodian] Batch starting after seq {cursor?.ToString() ?? "(start)"}: " +
                    $"fetched={batch.Count} decrypted={decrypted.Count} skipped={batchSkipped} " +
                    $"(noKey={skipNoKey}, decryptFail={skipDecryptFail})");
            }

            // ───── STAGE 2: ENCRYPT+SIGN IN SEQ ORDER (serializzato dalla catena) ─────
            // encrypt[i+1].PrevEnvelopeHash = EnvelopeHasher.Compute(rewrapped[i]): l'output di
            // encrypt[i] è input di encrypt[i+1], quindi non si può parallelizzare in chain.
            var rewrappedList = new List<MessageEnvelope>(batch.Count);
            foreach (var env in batch)
            {
                ct.ThrowIfCancellationRequested();
                if (env.EpochId >= newEpoch)
                {
                    // Envelope già all'epoch nuovo (posted da altri prima del FullReencrypt).
                    // Hash invariato sul server  il prossimo ri-cifrato non deve sovrascrivere
                    // il proprio prev (punterà a un hash invariato).
                    prevChainHashOverride = null;
                    continue;
                }
                if (!decrypted.TryGetValue(env.Seq, out var payload))
                {
                    // Salto (decrypt fallito): hash dell'envelope invariato sul server, stesso
                    // ragionamento di sopra.
                    prevChainHashOverride = null;
                    continue;
                }
                // oldCek serve a EncryptAndSign per ri-wrappare le DEK degli allegati (vedi
                // commento in ICustodianCryptoOffloader.EncryptAndSignAsync).
                var oldCek = keysByEpoch[env.EpochId];

                var enc = await _crypto.EncryptAndSignAsync(
                    payload, oldCek, newCek, signerDeviceId,
                    env.ConversationId, env.MessageId, env.Seq, newEpoch,
                    prevChainHashOverride, ct);

                var rewrapped = new MessageEnvelope
                {
                    ConversationId = env.ConversationId,
                    MessageId = env.MessageId,
                    Seq = env.Seq,
                    EpochId = newEpoch,
                    // L'envelope ri-cifrato è firmato e attestato dal device del custode (la firma
                    // originale dell'autore non è ricostruibile dopo la re-encrypt; cfr. redesign §7).
                    SenderDeviceId = signerDeviceId,
                    SenderUserId = signerUserId,
                    Nonce = enc.Nonce,
                    Ciphertext = enc.Ciphertext,
                    Signature = enc.Signature,
                    LeaseToken = string.Empty,
                    Type = env.Type
                };
                rewrappedList.Add(rewrapped);

                // Hash NUOVO dell'envelope appena costruito: è quello che il prossimo ri-cifrato
                // deve dichiarare come PrevEnvelopeHash, sostituendo il link stantio.
                prevChainHashOverride = EnvelopeHasher.Compute(rewrapped);
            }

            // ───── STAGE 3: POST DELLE SOSTITUZIONI IN PARALLELO ─────
            // Le repliche sono indipendenti server-side (chiavi per seq), quindi possiamo lanciare
            // fino a FullReencryptParallelism POST simultanei.
            var batchProcessed = 0;
            await Parallel.ForEachAsync(rewrappedList, parallelOpts, async (re, ict) =>
            {
                await _gateway.ReplaceMessageAsync(conversationId, re.Seq, re);
                Interlocked.Increment(ref batchProcessed);
            });
            processed += batchProcessed;

            cursor = batch[^1].Seq;
            batchId++;

            // Stima percentuale grezza: l'orchestrator registra quello che il custodian riporta.
            await _gateway.CheckpointMigrationAsync(
                conversationId, jobId, batchId, Math.Min(99, processed));

            // Aggiornamento progress per la UI dopo ogni batch: include skipped così l'utente
            // vede subito se qualche envelope sta venendo saltato (es. CEK mancante).
            progress?.Report(new MigrationProgress(MigrationPhase.Reencrypting, processed, total, skipped));

            if (batch.Count < FullReencryptBatchSize) break;
        }

        Console.WriteLine(
            $"[custodian] FullReencrypt LOOP DONE: conv={conversationId} jobId={jobId} " +
            $"processed={processed} skipped={skipped} total={total?.ToString() ?? "?"}");
    }
}
