using ECHAT.Models.Dtos;
using ECHAT.Server.App.Data;
using ECHAT.Server.App.Data.Entities;
using ECHAT.Server.Core.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace ECHAT.Server.App.Repositories;

/// <summary>
/// Implementazione EF di <see cref="IConversationPurger"/>: cancella in modo permanente tutte le
/// righe per-conversazione con <c>ExecuteDeleteAsync</c> (delete set-based, niente materializzazione
/// delle entità) dentro un'unica transazione, insieme alla riga di audit della cancellazione. Su un
/// provider relazionale (MySQL Pomelo) le delete e l'INSERT dell'audit si enlistano nella stessa
/// transazione esplicita, quindi l'operazione è atomica: o cancelliamo tutto + audit, o niente.
/// Il provider InMemory dei test NON supporta <c>ExecuteDeleteAsync</c> né le transazioni, quindi
/// lì si usa un fallback load + <c>RemoveRange</c> in un'unica <c>SaveChanges</c> (vedi branch sotto).
/// Il log di audit (append-only) NON viene toccato, a parte la riga di cancellazione aggiunta qui.
/// I blob cifrati su storage vengono cancellati DOPO il commit (non sono transazionali): un eventuale
/// fallimento lì lascia solo blob orfani, mai dati di conversazione parzialmente cancellati.
/// </summary>
public class ConversationPurger : IConversationPurger
{
    private readonly EchatDbContext _db;
    private readonly IBlobStorageService _blobStorage;

    public ConversationPurger(EchatDbContext db, IBlobStorageService blobStorage)
    {
        _db = db;
        _blobStorage = blobStorage;
    }

    public async Task PurgeAsync(Guid conversationId, AuditEntry deletionAudit)
    {
        // (a) Catturiamo gli id dei file PRIMA di cancellarne le righe: ci servono per ripulire i
        // blob cifrati su storage dopo il commit.
        var fileIds = await _db.Files
            .Where(f => f.ConversationId == conversationId)
            .Select(f => f.FileId)
            .ToListAsync();

        var auditRow = new AuditLogEntity
        {
            ConversationId = deletionAudit.ConversationId,
            UserId = deletionAudit.UserId,
            Action = deletionAudit.Action,
            Timestamp = deletionAudit.Timestamp,
            Details = deletionAudit.Details
        };

        if (_db.Database.IsRelational())
        {
            // (b) Produzione (MySQL): delete set-based con ExecuteDeleteAsync (niente
            // materializzazione delle entità) dentro un'unica transazione esplicita insieme
            // all'INSERT dell'audit. ExecuteDeleteAsync si enlista nella transazione aperta.
            await using var tx = await _db.Database.BeginTransactionAsync();

            await _db.Messages.Where(x => x.ConversationId == conversationId).ExecuteDeleteAsync();
            await _db.Members.Where(x => x.ConversationId == conversationId).ExecuteDeleteAsync();
            await _db.KeyEnvelopes.Where(x => x.ConversationId == conversationId).ExecuteDeleteAsync();
            await _db.Files.Where(x => x.ConversationId == conversationId).ExecuteDeleteAsync();
            await _db.SeqCounters.Where(x => x.ConversationId == conversationId).ExecuteDeleteAsync();
            await _db.SeqLeases.Where(x => x.ConversationId == conversationId).ExecuteDeleteAsync();
            await _db.ChainBoundaries.Where(x => x.ConversationId == conversationId).ExecuteDeleteAsync();
            await _db.MigrationJobs.Where(x => x.ConversationId == conversationId).ExecuteDeleteAsync();
            await _db.Conversations.Where(x => x.Id == conversationId).ExecuteDeleteAsync();

            _db.AuditLog.Add(auditRow);
            await _db.SaveChangesAsync();
            await tx.CommitAsync();
        }
        else
        {
            // (b') Provider InMemory (test): NON supporta ExecuteDeleteAsync né le transazioni.
            // Carichiamo e RemoveRange in un'unica SaveChanges (atomica nel suo store).
            _db.Messages.RemoveRange(await _db.Messages.Where(x => x.ConversationId == conversationId).ToListAsync());
            _db.Members.RemoveRange(await _db.Members.Where(x => x.ConversationId == conversationId).ToListAsync());
            _db.KeyEnvelopes.RemoveRange(await _db.KeyEnvelopes.Where(x => x.ConversationId == conversationId).ToListAsync());
            _db.Files.RemoveRange(await _db.Files.Where(x => x.ConversationId == conversationId).ToListAsync());
            _db.SeqCounters.RemoveRange(await _db.SeqCounters.Where(x => x.ConversationId == conversationId).ToListAsync());
            _db.SeqLeases.RemoveRange(await _db.SeqLeases.Where(x => x.ConversationId == conversationId).ToListAsync());
            _db.ChainBoundaries.RemoveRange(await _db.ChainBoundaries.Where(x => x.ConversationId == conversationId).ToListAsync());
            _db.MigrationJobs.RemoveRange(await _db.MigrationJobs.Where(x => x.ConversationId == conversationId).ToListAsync());
            _db.Conversations.RemoveRange(await _db.Conversations.Where(x => x.Id == conversationId).ToListAsync());

            _db.AuditLog.Add(auditRow);
            await _db.SaveChangesAsync();
        }

        // (c) DOPO il commit ripuliamo i blob cifrati: non sono transazionali, e farlo prima
        // rischierebbe di cancellare blob per una purga poi annullata.
        await _blobStorage.DeleteConversationAsync(conversationId, fileIds);
    }
}
