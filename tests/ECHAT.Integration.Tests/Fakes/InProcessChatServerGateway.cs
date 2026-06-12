using ECHAT.Client.Core.Interfaces;
using ECHAT.Models.Domain;
using ECHAT.Models.Dtos;
using ECHAT.Models.Enums;
using ECHAT.Server.App.Data;
using ECHAT.Server.App.Repositories;
using ECHAT.Server.Core.Interfaces;
using ServerIChatBoundaryStore = ECHAT.Server.Core.Interfaces.IChainBoundaryStore;

namespace ECHAT.Integration.Tests.Fakes;

/// <summary>
/// Implementazione di <see cref="IChatServerGateway"/> che bypassa HTTP e chiama direttamente
/// i service server-side. Serve a guidare <c>CustodianWorker</c> nei test integrazione senza
/// dover montare un'intera WebApplicationFactory.
///
/// Implementa solo i metodi che il flusso FullReencrypt usa; il resto throws NotImplemented
/// così se un test si appoggia a un metodo non simulato lo scopriamo subito.
/// </summary>
public class InProcessChatServerGateway : IChatServerGateway
{
    private readonly Guid _userId;
    private readonly IKeyEnvelopeStore _keys;
    private readonly IMigrationOrchestrator _orchestrator;
    private readonly IMigrationJobStore _jobs;
    private readonly ServerIChatBoundaryStore _chainBoundaries;
    private readonly MessageRepository _messages;

    // Test-only: in produzione ogni request HTTP ha la sua scope DI con il proprio DbContext.
    // Qui condividiamo un singolo DbContext fra tutti i gateway calls; siccome EF Core non
    // è thread-safe sul DbContext, dobbiamo serializzare. La CustodianWorker fa
    // Parallel.ForEachAsync sui POST (stage 3) e questo semaforo li mette in coda: non
    // riproduce il vero parallelismo HTTP, ma è fedele alla semantica per-request del
    // server.
    private readonly SemaphoreSlim _dbSemaphore = new(1, 1);

    public InProcessChatServerGateway(
        Guid userId,
        EchatDbContext db,
        IKeyEnvelopeStore keys,
        IMigrationOrchestrator orchestrator,
        IMigrationJobStore jobs,
        ServerIChatBoundaryStore chainBoundaries)
    {
        _userId = userId;
        _keys = keys;
        _orchestrator = orchestrator;
        _jobs = jobs;
        _chainBoundaries = chainBoundaries;
        _messages = new MessageRepository(db);
    }

    public Task<Guid> GetCurrentUserIdAsync() => Task.FromResult(_userId);

    private async Task<T> WithLockAsync<T>(Func<Task<T>> action)
    {
        await _dbSemaphore.WaitAsync();
        try { return await action(); }
        finally { _dbSemaphore.Release(); }
    }

    private async Task WithLockAsync(Func<Task> action)
    {
        await _dbSemaphore.WaitAsync();
        try { await action(); }
        finally { _dbSemaphore.Release(); }
    }

    public Task<List<WrappedKey>> GetKeysAsync(Guid conversationId, int? epochId = null)
        => WithLockAsync(() => _keys.GetKeysAsync(conversationId, epochId, _userId));

    public Task<List<MessageEnvelope>> FetchEnvelopesAsync(
        Guid conversationId, long? afterSeq, long? beforeSeq, int limit)
        => WithLockAsync(() => _messages.QueryAsync(conversationId, afterSeq, beforeSeq, limit));

    public Task<int> CountEnvelopesBelowEpochAsync(Guid conversationId, int epochBelow)
        => WithLockAsync(() => _messages.CountByEpochBelowAsync(conversationId, epochBelow));

    public Task ReplaceMessageAsync(Guid conversationId, long seq, MessageEnvelope envelope)
        => WithLockAsync(async () =>
        {
            // Replica la logica di MessagesController.Replace: solo il custode del job FullReencrypt
            // attivo può sostituire envelope, e ogni replace aggiorna MaxReplacedSeq sul job.
            var job = await _jobs.GetActiveFullReencryptJobAsync(conversationId)
                      ?? throw new InvalidOperationException("No active FullReencrypt job");
            if (job.CustodianUserId != _userId)
                throw new InvalidOperationException("Caller is not the custodian");

            await _messages.ReplaceAsync(seq, envelope);
            await _orchestrator.RecordReplacementAsync(job.Id, seq);
        });

    public Task<Guid> StartMigrationAsync(Guid conversationId, MigrationMode mode)
        => WithLockAsync(() => _orchestrator.StartMigrationAsync(conversationId, mode, _userId));

    public Task CheckpointMigrationAsync(Guid conversationId, Guid jobId, int batchId, int progressPercent)
        => WithLockAsync(() => _orchestrator.CheckpointAsync(conversationId, jobId, batchId, progressPercent));

    public Task FinalizeMigrationAsync(Guid conversationId, Guid jobId)
        => WithLockAsync(() => _orchestrator.FinalizeAsync(conversationId, jobId));

    public Task CancelMigrationAsync(Guid conversationId, Guid jobId)
        => WithLockAsync(() => _orchestrator.CancelAsync(conversationId, jobId));

    public Task ForceFinalizeMigrationAsync(Guid conversationId, Guid jobId)
        => WithLockAsync(() => _orchestrator.ForceFinalizeAsync(conversationId, jobId));

    public Task<List<ChainBoundary>> GetChainBoundariesAsync(Guid conversationId)
        => WithLockAsync(async () =>
        {
            var list = await _chainBoundaries.ListAsync(conversationId);
            return list.Select(b => new ChainBoundary(b.AfterSeq, b.AtEpoch, b.CreatedAt)).ToList();
        });

    // Non usati da FullReencrypt: throw esplicito se qualche test inavvertitamente li tocca.
    public Task<MessageEnvelope?> GetLatestEnvelopeAsync(Guid conversationId)
        => throw new NotImplementedException("GetLatestEnvelopeAsync not wired for these tests");
    public Task<SeqReservation> ReserveSeqAsync(Guid conversationId, int count)
        => throw new NotImplementedException("ReserveSeqAsync not wired for these tests");
    public Task PostMessageAsync(MessageEnvelope envelope)
        => throw new NotImplementedException("PostMessageAsync not wired for these tests");
    public Task ModerateMessageAsync(Guid conversationId, long seq, bool hidden, string? reason)
        => throw new NotImplementedException("ModerateMessageAsync not wired for these tests");
    public Task AddMemberAsync(Guid conversationId, Guid userId, bool includeHistory)
        => throw new NotImplementedException("AddMemberAsync not wired for these tests");
    public Task<int> RemoveMemberAsync(Guid conversationId, Guid userId)
        => throw new NotImplementedException("RemoveMemberAsync not wired for these tests");
    public Task SetMemberRoleAsync(Guid conversationId, Guid userId, string role)
        => throw new NotImplementedException("SetMemberRoleAsync not wired for these tests");
    public Task RenameConversationAsync(Guid conversationId, string newName)
        => throw new NotImplementedException("RenameConversationAsync not wired for these tests");
    public Task DeleteConversationAsync(Guid conversationId)
        => throw new NotImplementedException("DeleteConversationAsync not wired for these tests");
    public Task PostTombstonesAsync(Guid conversationId, IEnumerable<TombstoneRecord> tombstones)
        => throw new NotImplementedException("PostTombstonesAsync not wired for these tests");
    public Task<List<DevicePublicKey>> GetConversationDevicesAsync(Guid conversationId)
        => Task.FromResult(new List<DevicePublicKey>());
    public Task<DevicePublicKey?> GetConversationSenderDeviceAsync(Guid conversationId, Guid deviceId)
        => Task.FromResult<DevicePublicKey?>(null);

    public Task PostKeysAsync(Guid conversationId, List<WrappedKey> wraps)
        => WithLockAsync(() => _keys.StoreWrapsAsync(conversationId, wraps));
    public Task RegisterDeviceAsync(DeviceRegistration registration) => Task.CompletedTask;
}
