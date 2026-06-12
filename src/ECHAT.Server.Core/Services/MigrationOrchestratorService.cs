using ECHAT.Models.Enums;
using ECHAT.Models.Events;
using ECHAT.Server.Core.Exceptions;
using ECHAT.Server.Core.Interfaces;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ECHAT.Server.Core.Services;

public class MigrationOrchestratorService : IMigrationOrchestrator
{
    private readonly IMigrationJobStore _jobs;
    private readonly IMessageRepository _messages;
    private readonly IConversationReader _conversations;
    private readonly IKeyEnvelopeStore _keyStore;
    private readonly IChainBoundaryStore? _chainBoundaries;
    private readonly IRealtimeNotifier _notifier;
    private readonly ILogger<MigrationOrchestratorService> _logger;

    public MigrationOrchestratorService(
        IMigrationJobStore jobs,
        IMessageRepository messages,
        IConversationReader conversations,
        IKeyEnvelopeStore keyStore,
        IRealtimeNotifier notifier)
        : this(jobs, messages, conversations, keyStore, notifier,
               NullLogger<MigrationOrchestratorService>.Instance, chainBoundaries: null) { }

    public MigrationOrchestratorService(
        IMigrationJobStore jobs,
        IMessageRepository messages,
        IConversationReader conversations,
        IKeyEnvelopeStore keyStore,
        IRealtimeNotifier notifier,
        ILogger<MigrationOrchestratorService> logger,
        IChainBoundaryStore? chainBoundaries = null)
    {
        _jobs = jobs;
        _messages = messages;
        _conversations = conversations;
        _keyStore = keyStore;
        _notifier = notifier;
        _logger = logger;
        _chainBoundaries = chainBoundaries;
    }

    public async Task<Guid> StartMigrationAsync(Guid conversationId, MigrationMode mode, Guid custodianUserId)
    {
        // Solo FullReencrypt crea un job server-side: la saga è pilotata dal client custode
        // (checkpoint/replace/finalize). RewrapOnly NON ha lavoro server-side: la rotazione epoch
        // avviene in RemoveMember e la nuova CEK è wrappata client-side solo per i membri rimasti
        // (più lo shred dei wrap del rimosso, sempre in RemoveMember). Un job qui sarebbe puro
        // teatro di progresso, e resterebbe InProgress per sempre, bloccando il single-flight.
        if (mode != MigrationMode.FullReencrypt)
            throw new ValidationException(
                $"Migration mode '{mode}' does not require a server-side job. Only FullReencrypt is supported.");

        // Lock single-flight per conversazione (una migrazione attiva alla volta)
        if (await _jobs.HasActiveJobAsync(conversationId))
            throw new ConflictException("A migration job is already active for this conversation");

        var job = new MigrationJobRecord
        {
            Id = Guid.NewGuid(),
            ConversationId = conversationId,
            Mode = mode.ToString(),
            Status = "InProgress",
            ProgressPercent = 0,
            CreatedAt = DateTime.UtcNow,
            CustodianUserId = custodianUserId
        };

        await _jobs.CreateAsync(job);

        _logger.LogInformation(
            "Migration started: jobId={JobId} conversation={ConversationId} mode={Mode} custodian={CustodianUserId}",
            job.Id, conversationId, mode, custodianUserId);

        await NotifyProgressAsync(conversationId, job.Id, 0, "InProgress");

        // FullReencrypt: il client custode pilota il progresso via /checkpoint e POSTa le sostituzioni
        // su /messages/{seq}/replace. Il job resta InProgress finché il client non chiama /finalize.

        return job.Id;
    }

    public Task<MigrationJobRecord?> GetActiveFullReencryptJobAsync(Guid conversationId)
        => _jobs.GetActiveFullReencryptJobAsync(conversationId);

    public Task RecordReplacementAsync(Guid jobId, long seq)
        => _jobs.UpdateMaxReplacedSeqAsync(jobId, seq);

    public async Task CheckpointAsync(Guid conversationId, Guid jobId, int batchId, int progressPercent)
    {
        var job = await LoadJobForConversationAsync(conversationId, jobId);

        // Idempotenza: se il job è già chiuso (Completed/Cancelled/Failed), un checkpoint
        // in ritardo NON deve riportarlo a InProgress. Tipica race: l'utente clicca Cancel
        // mentre l'ultimo batch sta ancora chiamando checkpoint.
        if (IsTerminalStatus(job.Status)) return;

        job.Status = "InProgress";
        job.LastCheckpointBatchId = batchId;
        job.ProgressPercent = progressPercent;
        // ConcurrencyCheck su Status: se nel frattempo qualcun altro ha mosso il job a terminale,
        // la UPDATE non trova la row e TrySaveAsync ritorna false. Niente notify in quel caso.
        if (!await TrySaveAsync(job)) return;

        await NotifyProgressAsync(job.ConversationId, jobId, progressPercent, "InProgress");
    }

    public async Task CancelAsync(Guid conversationId, Guid jobId)
    {
        var job = await LoadJobForConversationAsync(conversationId, jobId);

        // Idempotenza: se già terminale, ritorniamo OK senza modificare lo stato.
        if (IsTerminalStatus(job.Status)) return;

        // Nessun crypto-shred: in stato cancellato il custode ha potenzialmente lasciato
        // envelope al vecchio epoch + envelope al nuovo, entrambe le CEK servono ancora.
        job.Status = "Cancelled";
        job.CompletedAt = DateTime.UtcNow;
        if (!await TrySaveAsync(job)) return;

        _logger.LogInformation(
            "Migration cancelled: jobId={JobId} conversation={ConversationId} lastProgress={Progress}",
            jobId, job.ConversationId, job.ProgressPercent);

        await NotifyProgressAsync(job.ConversationId, jobId, job.ProgressPercent, "Cancelled");
    }

    public Task FinalizeAsync(Guid conversationId, Guid jobId) => FinalizeCoreAsync(conversationId, jobId, force: false);

    public Task ForceFinalizeAsync(Guid conversationId, Guid jobId) => FinalizeCoreAsync(conversationId, jobId, force: true);

    private async Task FinalizeCoreAsync(Guid conversationId, Guid jobId, bool force)
    {
        var job = await LoadJobForConversationAsync(conversationId, jobId);

        // Idempotenza: se già terminale, ritorniamo OK.
        if (IsTerminalStatus(job.Status)) return;

        var currentEpoch = await _conversations.GetCurrentEpochAsync(job.ConversationId)
            ?? throw new InvalidOperationException($"Conversation {job.ConversationId} not found");

        // Safety check normale (skip se force=true): per FullReencrypt rifiutiamo il finalize
        // finché ci sono envelope a epoch < current. Il crypto-shred che segue cancella le
        // CEK vecchie; se qualche envelope NON è stato ri-cifrato, shred quelle CEK lo rende
        // PERMANENTEMENTE illeggibile.
        //
        // Con force=true il chiamante ha esplicitamente accettato la perdita (vedi
        // ForceFinalizeAsync). Tipico use case: il custode non è l'Owner e non ha le CEK
        // per gli epoch più vecchi, quindi non può completare la rewrite. L'unica
        // alternativa è cancellare la saga e lasciare la conversazione in stato misto
        // (vecchi membri ancora in grado di leggere via CEK cached).
        if (!force && job.Mode == nameof(MigrationMode.FullReencrypt))
        {
            var remaining = await _messages.CountByEpochBelowAsync(job.ConversationId, currentEpoch);
            if (remaining > 0)
            {
                _logger.LogWarning(
                    "FinalizeAsync refused: jobId={JobId} conversation={ConversationId} envelopesAtOldEpoch={Remaining}; custodian must resume re-encryption or call ForceFinalize",
                    jobId, job.ConversationId, remaining);
                throw new ConflictException(
                    $"Cannot finalize: {remaining} envelope(s) at epoch < {currentEpoch}. Custodian must finish re-encryption.");
            }
        }
        else if (force && job.Mode == nameof(MigrationMode.FullReencrypt))
        {
            // Log la perdita esplicita prima di shred-are: questo deve restare visibile
            // anche se l'audit del controller dovesse perdersi.
            var lost = await _messages.CountByEpochBelowAsync(job.ConversationId, currentEpoch);
            _logger.LogWarning(
                "ForceFinalize accepted DATA LOSS: jobId={JobId} conversation={ConversationId} unreadableEnvelopes={Lost}",
                jobId, job.ConversationId, lost);
        }

        // Crypto-shred: cancella ogni CEK wrappata per gli epoch precedenti a quello corrente.
        var allKeys = await _keyStore.GetKeysAsync(job.ConversationId, epochId: null, deviceId: null);
        var oldEpochs = allKeys.Where(k => k.EpochId < currentEpoch)
            .Select(k => k.EpochId)
            .Distinct()
            .ToList();

        foreach (var epoch in oldEpochs)
            await _keyStore.DeleteWrapsAsync(job.ConversationId, epoch, deviceId: null);

        // ChainBoundary: dopo FullReencrypt con riscritture, scriviamo un boundary in modo che il
        // client validator non urli "Chain: Break" sull'envelope subito dopo l'ultimo sostituito.
        if (job.Mode == nameof(MigrationMode.FullReencrypt) && job.MaxReplacedSeq > 0 && _chainBoundaries is not null)
        {
            await _chainBoundaries.AddAsync(job.ConversationId, job.MaxReplacedSeq, currentEpoch);
            _logger.LogInformation(
                "ChainBoundary written: conversation={ConversationId} afterSeq={AfterSeq} atEpoch={Epoch}",
                job.ConversationId, job.MaxReplacedSeq, currentEpoch);
        }

        job.Status = "Completed";
        job.CompletedAt = DateTime.UtcNow;
        job.ProgressPercent = 100;
        if (!await TrySaveAsync(job)) return;

        _logger.LogInformation(
            "Migration finalized: jobId={JobId} conversation={ConversationId} shreddedEpochs={EpochCount} maxReplacedSeq={MaxReplacedSeq}",
            jobId, job.ConversationId, oldEpochs.Count, job.MaxReplacedSeq);

        await NotifyProgressAsync(job.ConversationId, jobId, 100, "Completed");
    }

    /// <summary>
    /// Wrapper di SaveAsync che ingloba la DbUpdateConcurrencyException sollevata dal
    /// [ConcurrencyCheck] su Status. Ritorna false se un'altra transazione ha vinto la corsa;
    /// il chiamante interpreta "vinto da altri" come idempotenza (return senza notify).
    /// </summary>
    private async Task<bool> TrySaveAsync(MigrationJobRecord job)
    {
        try
        {
            await _jobs.SaveAsync(job);
            return true;
        }
        catch (ConcurrencyConflictException)
        {
            _logger.LogInformation(
                "Concurrent terminal write won: jobId={JobId} desiredStatus={Status}",
                job.Id, job.Status);
            return false;
        }
    }

    private static bool IsTerminalStatus(string status)
        => status is "Completed" or "Cancelled" or "Failed";

    /// <summary>
    /// Carica il job e ASSERISCE che appartenga alla conversazione della route. Difesa IDOR:
    /// il filtro <c>[RequireConversationPermission(Permission.Admin)]</c> prova solo che il
    /// chiamante è admin della conversazione di <paramref name="conversationId"/>, NON che il
    /// jobId sia di quella conversazione. Senza questo controllo un admin potrebbe passare il
    /// jobId di un'altra conversazione e finalize/cancel/crypto-shred-arla. Trattiamo sia il
    /// job inesistente sia il mismatch come 404 (NotFoundException) per non rivelare l'esistenza
    /// di job altrui.
    /// </summary>
    private async Task<MigrationJobRecord> LoadJobForConversationAsync(Guid conversationId, Guid jobId)
    {
        var job = await _jobs.GetByIdAsync(jobId);
        if (job is null || job.ConversationId != conversationId)
            throw new NotFoundException($"Migration job {jobId} not found");
        return job;
    }

    private Task NotifyProgressAsync(Guid conversationId, Guid jobId, int percent, string status)
        => _notifier.NotifyAsync(conversationId, new JobProgressEvent
        {
            ConversationId = conversationId,
            JobId = jobId,
            ProgressPercent = percent,
            Status = status,
            Timestamp = DateTime.UtcNow
        });
}
