using ECHAT.Server.App.Data;
using ECHAT.Server.App.Data.Entities;
using ECHAT.Server.App.Repositories;
using ECHAT.Server.Core.Services;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace ECHAT.Integration.Tests;

public class RetentionServiceTests : IDisposable
{
    private readonly EchatDbContext _db;
    private readonly SeqLeaseStore _leases;
    private readonly AuditLogRepository _audit;

    public RetentionServiceTests()
    {
        var options = new DbContextOptionsBuilder<EchatDbContext>()
            .UseInMemoryDatabase($"echat-{Guid.NewGuid()}")
            .Options;
        _db = new EchatDbContext(options);
        _leases = new SeqLeaseStore(_db);
        _audit = new AuditLogRepository(_db);
    }

    public void Dispose() => _db.Dispose();

    private RetentionService Sut(TimeSpan? auditMaxAge = null) =>
        new(_leases, _audit, auditMaxAge);

    [Fact]
    public async Task PurgeExpiredLeases_RemovesExpired_KeepsActive()
    {
        var now = DateTime.UtcNow;
        _db.SeqLeases.AddRange(
            new SeqLeaseEntity { LeaseToken = "expired1", ExpiresAt = now.AddSeconds(-10) },
            new SeqLeaseEntity { LeaseToken = "expired2", ExpiresAt = now.AddMinutes(-5) },
            new SeqLeaseEntity { LeaseToken = "active", ExpiresAt = now.AddMinutes(5) });
        await _db.SaveChangesAsync();

        var purged = await Sut().PurgeExpiredLeasesAsync(now);

        purged.Should().Be(2);
        (await _db.SeqLeases.Select(l => l.LeaseToken).ToListAsync())
            .Should().BeEquivalentTo(new[] { "active" });
    }

    [Fact]
    public async Task PurgeExpiredLeases_NothingToPurge_ReturnsZero()
    {
        var purged = await Sut().PurgeExpiredLeasesAsync(DateTime.UtcNow);
        purged.Should().Be(0);
    }

    [Fact]
    public async Task PurgeOldAuditLog_RemovesOlderThanWindow()
    {
        var now = DateTime.UtcNow;
        var sut = Sut(auditMaxAge: TimeSpan.FromDays(30));

        _db.AuditLog.AddRange(
            new AuditLogEntity { Action = "old", Timestamp = now.AddDays(-31) },
            new AuditLogEntity { Action = "ancient", Timestamp = now.AddDays(-365) },
            new AuditLogEntity { Action = "fresh", Timestamp = now.AddDays(-1) });
        await _db.SaveChangesAsync();

        var purged = await sut.PurgeOldAuditLogAsync(now);

        purged.Should().Be(2);
        (await _db.AuditLog.Select(a => a.Action).ToListAsync())
            .Should().BeEquivalentTo(new[] { "fresh" });
    }

    [Fact]
    public async Task PurgeOldAuditLog_EmptyTable_ReturnsZero()
    {
        var purged = await Sut().PurgeOldAuditLogAsync(DateTime.UtcNow);
        purged.Should().Be(0);
    }
}
