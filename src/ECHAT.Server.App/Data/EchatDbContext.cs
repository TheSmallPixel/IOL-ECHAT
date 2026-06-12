using ECHAT.Server.App.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace ECHAT.Server.App.Data;

public class EchatDbContext : DbContext
{
    public EchatDbContext(DbContextOptions<EchatDbContext> options) : base(options) { }

    public DbSet<MessageEntity> Messages => Set<MessageEntity>();
    public DbSet<ConversationEntity> Conversations => Set<ConversationEntity>();
    public DbSet<MemberEntity> Members => Set<MemberEntity>();
    public DbSet<KeyEnvelopeEntity> KeyEnvelopes => Set<KeyEnvelopeEntity>();
    public DbSet<FileEntity> Files => Set<FileEntity>();
    public DbSet<AuditLogEntity> AuditLog => Set<AuditLogEntity>();
    public DbSet<SeqCounterEntity> SeqCounters => Set<SeqCounterEntity>();
    public DbSet<MigrationJobEntity> MigrationJobs => Set<MigrationJobEntity>();
    public DbSet<ChainBoundaryEntity> ChainBoundaries => Set<ChainBoundaryEntity>();
    public DbSet<UserEntity> Users => Set<UserEntity>();
    public DbSet<SeqLeaseEntity> SeqLeases => Set<SeqLeaseEntity>();
    public DbSet<DevicePublicKeyEntity> DevicePublicKeys => Set<DevicePublicKeyEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<MessageEntity>(e =>
        {
            e.HasKey(m => m.Id);
            e.HasIndex(m => new { m.ConversationId, m.Seq }).IsUnique();
            e.HasIndex(m => m.MessageId).IsUnique();
        });

        modelBuilder.Entity<ConversationEntity>(e =>
        {
            e.HasKey(c => c.Id);
        });

        modelBuilder.Entity<MemberEntity>(e =>
        {
            e.HasKey(m => m.Id);
            e.HasIndex(m => new { m.ConversationId, m.UserId }).IsUnique();
        });

        modelBuilder.Entity<KeyEnvelopeEntity>(e =>
        {
            e.HasKey(k => k.Id);
            // UNIQUE: un solo wrap per (conversation, epoch, device). Rende l'upsert deterministico e
            // impedisce righe duplicate da POST /keys concorrenti (un perdente prende duplicate-key).
            e.HasIndex(k => new { k.ConversationId, k.EpochId, k.DeviceId }).IsUnique();
        });

        modelBuilder.Entity<FileEntity>(e =>
        {
            e.HasKey(f => f.Id);
            e.HasIndex(f => f.FileId).IsUnique();
        });

        modelBuilder.Entity<AuditLogEntity>(e =>
        {
            e.HasKey(a => a.Id);
            e.HasIndex(a => a.ConversationId);
        });

        modelBuilder.Entity<SeqCounterEntity>(e =>
        {
            e.HasKey(s => s.ConversationId);
        });

        modelBuilder.Entity<MigrationJobEntity>(e =>
        {
            e.HasKey(j => j.Id);
            e.HasIndex(j => j.ConversationId);
        });

        modelBuilder.Entity<ChainBoundaryEntity>(e =>
        {
            e.HasKey(b => b.Id);
            e.HasIndex(b => new { b.ConversationId, b.AfterSeq });
        });

        modelBuilder.Entity<UserEntity>(e =>
        {
            e.HasKey(u => u.Id);
            e.HasIndex(u => u.GoogleSubjectId).IsUnique();
            e.HasIndex(u => u.Email).IsUnique();
        });

        modelBuilder.Entity<SeqLeaseEntity>(e =>
        {
            e.HasKey(l => l.Id);
            e.HasIndex(l => l.LeaseToken).IsUnique();
            e.HasIndex(l => new { l.ConversationId, l.LeaseToken });
        });

        modelBuilder.Entity<DevicePublicKeyEntity>(e =>
        {
            e.HasKey(d => d.Id);
            // Non-unique: la rotazione soft-revoca le righe vecchie, quindi possono coesistere più
            // righe per DeviceId (al più una con RevokedAt == null). Pomelo/MySQL non supporta indici
            // filtrati, perciò l'unicità "una sola attiva" è garantita a livello applicativo (UpsertAsync).
            e.HasIndex(d => d.DeviceId);
            e.HasIndex(d => d.UserId);
        });
    }
}
