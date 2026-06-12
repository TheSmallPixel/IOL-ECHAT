using ECHAT.Models.Dtos;
using ECHAT.Server.App.Data;
using ECHAT.Server.App.Data.Entities;
using ECHAT.Server.App.Repositories;
using ECHAT.Server.Core.Exceptions;
using ECHAT.Server.Core.Services;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace ECHAT.Integration.Tests;

// Il gating PlatformAdmin non è più responsabilità di questo service ma del filtro
// [RequirePlatformAdmin] sul controller: la mappa claim->decisione è coperta da
// PolicyEnforcerTests.AuthorizePlatform_* e lo short-circuit dal filtro in PlatformAdminFilterTests.
// Qui restano solo logica funzionale e regole di validazione/business (ruoli validi, utente
// inesistente, clamp dei limiti, filtri di audit).
public class PlatformStatisticsServiceTests : IDisposable
{
    private readonly EchatDbContext _db;

    public PlatformStatisticsServiceTests()
    {
        var options = new DbContextOptionsBuilder<EchatDbContext>()
            .UseInMemoryDatabase($"echat-{Guid.NewGuid()}")
            .Options;
        _db = new EchatDbContext(options);
    }

    public void Dispose() => _db.Dispose();

    private PlatformStatisticsService Sut() =>
        new(new UserStore(_db, new UserUpsertService()), new AuditLogRepository(_db));

    private async Task<Guid> SeedUser(string email, string role = "User", bool active = true)
    {
        var id = Guid.NewGuid();
        _db.Users.Add(new UserEntity
        {
            Id = id, GoogleSubjectId = id.ToString(), Email = email, DisplayName = email,
            PlatformRole = role, IsActive = active, CreatedAt = DateTime.UtcNow
        });
        await _db.SaveChangesAsync();
        return id;
    }

    [Fact]
    public async Task GetStats_CountsOnlyActiveUsers()
    {
        await SeedUser("u1@x");
        await SeedUser("u2@x", active: false); // inattivo non conta

        var stats = await Sut().GetStatsAsync();

        stats.TotalUsers.Should().Be(1);
        stats.TotalConversations.Should().Be(0);
        stats.TotalMessages.Should().Be(0);
    }

    [Fact]
    public async Task ListUsers_ReturnsAllUsers()
    {
        await SeedUser("u1@x");
        await SeedUser("u2@x");

        var users = await Sut().ListUsersAsync();

        users.Should().HaveCount(2);
    }

    // Seed di un messaggio: SenderUserId è l'identità reale; SenderDeviceId è un device fisico
    // DIVERSO dallo userId (random) per provare che il conteggio non si appoggi al device id.
    private async Task SeedMessage(Guid senderUserId, long seq)
    {
        _db.Messages.Add(new MessageEntity
        {
            ConversationId = Guid.NewGuid(),
            MessageId = Guid.NewGuid(),
            Seq = seq,
            EpochId = 1,
            SenderDeviceId = Guid.NewGuid(), // device fisico ≠ userId
            SenderUserId = senderUserId,
            CreatedAt = DateTime.UtcNow,
        });
        await _db.SaveChangesAsync();
    }

    [Fact]
    public async Task MessageCount_IsPerSenderUser_NotPerDevice()
    {
        var u1 = await SeedUser("u1@x");
        var u2 = await SeedUser("u2@x");
        await SeedMessage(u1, 1);
        await SeedMessage(u1, 2);
        await SeedMessage(u2, 3);

        // ListUsersWithActivity: il conteggio per-utente deve riflettere SenderUserId.
        var users = await Sut().ListUsersAsync();
        users.Single(u => u.Id == u1).MessageCount.Should().Be(2);
        users.Single(u => u.Id == u2).MessageCount.Should().Be(1);

        // GetUserDetail: stesso conteggio per il singolo utente.
        (await Sut().GetUserDetailAsync(u1)).MessageCount.Should().Be(2);

        // E il totale di piattaforma resta il conteggio grezzo dei messaggi.
        (await Sut().GetStatsAsync()).TotalMessages.Should().Be(3);
    }

    [Fact]
    public async Task GetUserDetail_UnknownUser_ThrowsNotFound()
    {
        Func<Task> act = async () => await Sut().GetUserDetailAsync(Guid.NewGuid());
        await act.Should().ThrowAsync<NotFoundException>();
    }

    [Fact]
    public async Task GetUserDetail_ReturnsDetail()
    {
        var user = await SeedUser("u@x");

        var detail = await Sut().GetUserDetailAsync(user);
        detail.Id.Should().Be(user);
        detail.Email.Should().Be("u@x");
    }

    [Fact]
    public async Task SetUserRole_ValidRoles_Succeed()
    {
        var user = await SeedUser("u@x");

        await Sut().SetUserRoleAsync(user, "PlatformAdmin");
        (await _db.Users.FindAsync(user))!.PlatformRole.Should().Be("PlatformAdmin");

        await Sut().SetUserRoleAsync(user, "User");
        (await _db.Users.FindAsync(user))!.PlatformRole.Should().Be("User");
    }

    [Fact]
    public async Task SetUserRole_InvalidRole_Throws()
    {
        var user = await SeedUser("u@x");

        Func<Task> act = async () => await Sut().SetUserRoleAsync(user, "SuperGod");
        await act.Should().ThrowAsync<ValidationException>();
    }

    [Fact]
    public async Task SetUserRole_UnknownUser_ThrowsNotFound()
    {
        Func<Task> act = async () => await Sut().SetUserRoleAsync(Guid.NewGuid(), "User");
        await act.Should().ThrowAsync<NotFoundException>();
    }

    [Fact]
    public async Task SetUserActive_TogglesIsActive()
    {
        var user = await SeedUser("u@x");

        await Sut().SetUserActiveAsync(user, active: false);
        (await _db.Users.FindAsync(user))!.IsActive.Should().BeFalse();

        await Sut().SetUserActiveAsync(user, active: true);
        (await _db.Users.FindAsync(user))!.IsActive.Should().BeTrue();
    }

    [Fact]
    public async Task SetUserActive_UnknownUser_Throws()
    {
        Func<Task> act = async () => await Sut().SetUserActiveAsync(Guid.NewGuid(), false);
        await act.Should().ThrowAsync<NotFoundException>();
    }

    // --- Audit query ---

    private async Task SeedAudit(Guid conversationId, Guid? userId, string action, DateTime ts, string? details = null)
    {
        _db.AuditLog.Add(new AuditLogEntity
        {
            ConversationId = conversationId, UserId = userId,
            Action = action, Timestamp = ts, Details = details
        });
        await _db.SaveChangesAsync();
    }

    [Fact]
    public async Task QueryAudit_NoFilter_ReturnsAllOrderedDesc()
    {
        var conv = Guid.NewGuid();
        var actor = Guid.NewGuid();
        await SeedAudit(conv, actor, "MessageIngested", DateTime.UtcNow.AddHours(-3));
        await SeedAudit(conv, actor, "MemberAdded",    DateTime.UtcNow.AddHours(-1));
        await SeedAudit(conv, actor, "MemberRemoved",  DateTime.UtcNow.AddMinutes(-5));

        var entries = await Sut().QueryAuditAsync(new AuditQueryFilter());

        entries.Should().HaveCount(3);
        // Ordine desc per timestamp: più recente in cima.
        entries[0].Action.Should().Be("MemberRemoved");
        entries[2].Action.Should().Be("MessageIngested");
    }

    [Fact]
    public async Task QueryAudit_ActionFilter_NarrowsResultSet()
    {
        var conv = Guid.NewGuid();
        var actor = Guid.NewGuid();
        await SeedAudit(conv, actor, "MessageIngested", DateTime.UtcNow);
        await SeedAudit(conv, actor, "MemberAdded",    DateTime.UtcNow);
        await SeedAudit(conv, actor, "MemberAdded",    DateTime.UtcNow);

        var entries = await Sut().QueryAuditAsync(new AuditQueryFilter(Action: "MemberAdded"));

        entries.Should().HaveCount(2);
        entries.Should().OnlyContain(e => e.Action == "MemberAdded");
    }

    [Fact]
    public async Task QueryAudit_SinceFilter_DropsOlderEntries()
    {
        var conv = Guid.NewGuid();
        var actor = Guid.NewGuid();
        await SeedAudit(conv, actor, "OldEvent",    DateTime.UtcNow.AddDays(-3));
        await SeedAudit(conv, actor, "RecentEvent", DateTime.UtcNow.AddMinutes(-5));

        var entries = await Sut().QueryAuditAsync(new AuditQueryFilter(Since: DateTime.UtcNow.AddDays(-1)));

        entries.Should().HaveCount(1);
        entries[0].Action.Should().Be("RecentEvent");
    }

    [Fact]
    public async Task QueryAudit_ConversationAndUserFilters_Combine()
    {
        var convA = Guid.NewGuid();
        var convB = Guid.NewGuid();
        var u1 = Guid.NewGuid();
        var u2 = Guid.NewGuid();
        await SeedAudit(convA, u1, "X", DateTime.UtcNow);
        await SeedAudit(convA, u2, "X", DateTime.UtcNow);
        await SeedAudit(convB, u1, "X", DateTime.UtcNow);

        var entries = await Sut().QueryAuditAsync(new AuditQueryFilter(ConversationId: convA, UserId: u1));

        entries.Should().HaveCount(1);
        entries[0].ConversationId.Should().Be(convA);
        entries[0].UserId.Should().Be(u1);
    }

    [Fact]
    public async Task QueryAudit_LimitClampedAt500()
    {
        var conv = Guid.NewGuid();
        // Seed 510 entries: limit chiesto 10_000, server deve clampare a 500.
        for (int i = 0; i < 510; i++)
            _db.AuditLog.Add(new AuditLogEntity
            {
                ConversationId = conv, UserId = Guid.NewGuid(),
                Action = "Spam", Timestamp = DateTime.UtcNow.AddSeconds(-i)
            });
        await _db.SaveChangesAsync();

        var entries = await Sut().QueryAuditAsync(new AuditQueryFilter(Limit: 10_000));

        entries.Should().HaveCount(500);
    }

    [Fact]
    public async Task QueryAudit_LimitZero_ClampedToOne()
    {
        var conv = Guid.NewGuid();
        await SeedAudit(conv, Guid.NewGuid(), "X", DateTime.UtcNow);

        var entries = await Sut().QueryAuditAsync(new AuditQueryFilter(Limit: 0));

        // Math.Clamp(0, 1, 500) == 1: il chiamante che passa zero per errore vede comunque la
        // riga più recente invece di un set vuoto.
        entries.Should().HaveCount(1);
    }
}
