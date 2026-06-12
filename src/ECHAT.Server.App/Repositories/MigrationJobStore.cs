using ECHAT.Server.App.Data;
using ECHAT.Server.App.Data.Entities;
using ECHAT.Server.Core.Exceptions;
using ECHAT.Server.Core.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace ECHAT.Server.App.Repositories;

public class MigrationJobStore : IMigrationJobStore
{
    private readonly EchatDbContext _db;

    public MigrationJobStore(EchatDbContext db)
    {
        _db = db;
    }

    public Task<bool> HasActiveJobAsync(Guid conversationId)
        => _db.MigrationJobs.AnyAsync(j =>
            j.ConversationId == conversationId
            && j.Status != "Completed"
            && j.Status != "Cancelled"
            && j.Status != "Failed");

    public async Task CreateAsync(MigrationJobRecord job)
    {
        _db.MigrationJobs.Add(new MigrationJobEntity
        {
            Id = job.Id,
            ConversationId = job.ConversationId,
            Mode = job.Mode,
            Status = job.Status,
            ProgressPercent = job.ProgressPercent,
            LastCheckpointBatchId = job.LastCheckpointBatchId,
            CreatedAt = job.CreatedAt,
            CompletedAt = job.CompletedAt,
            CustodianUserId = job.CustodianUserId,
            MaxReplacedSeq = job.MaxReplacedSeq
        });
        await _db.SaveChangesAsync();
    }

    public async Task<MigrationJobRecord?> GetByIdAsync(Guid jobId)
    {
        var entity = await _db.MigrationJobs.FindAsync(jobId);
        return entity is null ? null : Map(entity);
    }

    public async Task SaveAsync(MigrationJobRecord job)
    {
        var entity = await _db.MigrationJobs.FindAsync(job.Id);
        if (entity is null)
            throw new InvalidOperationException($"Migration job {job.Id} not found");

        entity.Status = job.Status;
        entity.ProgressPercent = job.ProgressPercent;
        entity.LastCheckpointBatchId = job.LastCheckpointBatchId;
        entity.CompletedAt = job.CompletedAt;
        entity.MaxReplacedSeq = job.MaxReplacedSeq;
        // [ConcurrencyCheck] su Status: la UPDATE include WHERE Status=@old. Se nel frattempo
        // un'altra transazione ha mosso lo status (es. Cancel parallelo a Finalize), EF
        // rileva 0 righe affette e tira DbUpdateConcurrencyException. La incapsuliamo in una
        // ConcurrencyConflictException Core-layer così che Server.Core non dipenda da EF.
        try
        {
            await _db.SaveChangesAsync();
        }
        catch (DbUpdateConcurrencyException ex)
        {
            throw new ConcurrencyConflictException($"MigrationJob {job.Id} status changed concurrently") { Source = ex.Source };
        }
    }

    public async Task<MigrationJobRecord?> GetActiveFullReencryptJobAsync(Guid conversationId)
    {
        var entity = await _db.MigrationJobs
            .Where(j => j.ConversationId == conversationId
                     && j.Mode == "FullReencrypt"
                     && j.Status != "Completed"
                     && j.Status != "Cancelled"
                     && j.Status != "Failed")
            .OrderByDescending(j => j.CreatedAt)
            .FirstOrDefaultAsync();
        return entity is null ? null : Map(entity);
    }

    public async Task UpdateMaxReplacedSeqAsync(Guid jobId, long seq)
    {
        // Load+modify+save: in produzione due replace concorrenti su HTTP request diversi
        // possono leggere lo stesso MaxReplacedSeq e fare due UPDATE last-writer-wins. L'impatto
        // peggiore è un ChainBoundary off-by-few-seq al finalize. Atomicità vera richiederebbe
        // ExecuteUpdateAsync (non supportato da EF InMemory usato nei test) o una transazione
        // SERIALIZABLE. Accettabile per ora.
        var entity = await _db.MigrationJobs.FindAsync(jobId);
        if (entity is null) return;
        if (seq > entity.MaxReplacedSeq)
        {
            entity.MaxReplacedSeq = seq;
            await _db.SaveChangesAsync();
        }
    }

    private static MigrationJobRecord Map(MigrationJobEntity entity) => new()
    {
        Id = entity.Id,
        ConversationId = entity.ConversationId,
        Mode = entity.Mode,
        Status = entity.Status,
        ProgressPercent = entity.ProgressPercent,
        LastCheckpointBatchId = entity.LastCheckpointBatchId,
        CreatedAt = entity.CreatedAt,
        CompletedAt = entity.CompletedAt,
        CustodianUserId = entity.CustodianUserId,
        MaxReplacedSeq = entity.MaxReplacedSeq
    };
}
