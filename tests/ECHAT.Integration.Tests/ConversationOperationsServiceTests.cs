using ECHAT.Models.Dtos;
using ECHAT.Models.Events;
using ECHAT.Server.App.Data;
using ECHAT.Server.App.Data.Entities;
using ECHAT.Server.App.Repositories;
using ECHAT.Server.Core.Exceptions;
using ECHAT.Server.Core.Interfaces;
using ECHAT.Server.Core.Services;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Moq;

namespace ECHAT.Integration.Tests;

public class ConversationOperationsServiceTests : IDisposable
{
    private readonly EchatDbContext _db;
    private readonly Mock<IRealtimeNotifier> _notifier = new();
    private readonly AuditLogRepository _audit;

    public ConversationOperationsServiceTests()
    {
        var options = new DbContextOptionsBuilder<EchatDbContext>()
            .UseInMemoryDatabase($"echat-{Guid.NewGuid()}")
            .Options;
        _db = new EchatDbContext(options);
        _audit = new AuditLogRepository(_db);
    }

    public void Dispose() => _db.Dispose();

    // E2EE (S1): il service non gestisce più le CEK (niente ConversationKeyService). La distribuzione
    // delle chiavi è client-side (CekProvisioner), testata separatamente. Lo shred dei wrap del
    // membro rimosso invece è server-side (RemoveMember), quindi qui servono key + device store reali.
    private ConversationOperationsService Sut() => new(
        new ConversationStore(_db),
        new MemberStore(_db),
        new UserStore(_db, new ECHAT.Server.Core.Services.UserUpsertService()),
        new SeqCounterStore(_db, new ECHAT.Server.Core.Services.SeqCounterDomainService()),
        _audit,
        _notifier.Object,
        new ConversationPurger(_db, new FakeBlobStorage()),
        new KeyEnvelopeRepository(_db),
        new DevicePublicKeyStore(_db),
        Microsoft.Extensions.Logging.Abstractions.NullLogger<ConversationOperationsService>.Instance);

    /// <summary>
    /// Fake no-op di <see cref="IBlobStorageService"/>: la purga chiama solo DeleteConversationAsync,
    /// e qui non c'è uno storage di blob reale da toccare. Registra le chiamate di delete così i test
    /// possono verificare che la purga propaghi gli id dei file.
    /// </summary>
    private sealed class FakeBlobStorage : IBlobStorageService
    {
        public List<(Guid ConversationId, IReadOnlyCollection<Guid> FileIds)> DeleteCalls { get; } = new();

        public Task<FileUploadSession> BeginUploadAsync(Guid conversationId, Guid ownerUserId)
            => Task.FromResult(new FileUploadSession());
        public Task StorePartAsync(Guid conversationId, Guid userId, Guid fileId, string uploadToken, int partNo, byte[] encryptedBytes)
            => Task.CompletedTask;
        public Task<FileCommitResult> FinalizeAsync(Guid conversationId, Guid userId, Guid fileId, string uploadToken)
            => Task.FromResult(new FileCommitResult());
        public Task<Stream> ReadAsync(Guid conversationId, Guid fileId)
            => Task.FromResult<Stream>(new MemoryStream());
        public Task DeleteConversationAsync(Guid conversationId, IReadOnlyCollection<Guid> fileIds)
        {
            DeleteCalls.Add((conversationId, fileIds));
            return Task.CompletedTask;
        }
    }

    private async Task<Guid> SeedUser(string email = "u@x")
    {
        var id = Guid.NewGuid();
        _db.Users.Add(new UserEntity
        {
            Id = id, GoogleSubjectId = id.ToString(), Email = email, DisplayName = email,
            CreatedAt = DateTime.UtcNow
        });
        await _db.SaveChangesAsync();
        return id;
    }

    [Fact]
    public async Task Create_PersistsConversation_Member_SeqCounter_AndAudit()
    {
        var creator = await SeedUser("creator@x");

        var conv = await Sut().CreateAsync("Stanza", creator);

        conv.Name.Should().Be("Stanza");
        conv.CurrentEpochId.Should().Be(1);
        conv.CreatedByUserId.Should().Be(creator);

        (await _db.Members.SingleAsync()).Role.Should().Be("Owner");
        (await _db.SeqCounters.SingleAsync()).ConversationId.Should().Be(conv.Id);
        // E2EE (S1): il server NON crea più la CEK alla creazione, la provisiona il client.
        (await _db.KeyEnvelopes.CountAsync()).Should().Be(0);
        (await _db.AuditLog.SingleAsync()).Action.Should().Be("ConversationCreated");
    }

    [Fact]
    public async Task Create_NullName_UsesDefaultLabel()
    {
        var creator = await SeedUser();

        var conv = await Sut().CreateAsync(null, creator);

        conv.Name.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task List_ReturnsOnlyConversationsWhereUserIsActiveMember()
    {
        var alice = await SeedUser("alice@x");
        var bob = await SeedUser("bob@x");

        var c1 = await Sut().CreateAsync("c1", alice);
        var c2 = await Sut().CreateAsync("c2", bob);

        var aliceList = await Sut().ListForUserAsync(alice);
        aliceList.Should().ContainSingle().Which.Id.Should().Be(c1.Id);
    }

    // Nota: l'autorizzazione di accesso del *richiedente* (membership attiva / ruolo Owner-Admin)
    // non è più responsabilità del service ma del filtro [RequireConversationPermission] sul
    // controller. La sua logica è coperta da PolicyEnforcerTests (mappa ruolo->permesso) e dai
    // test del filtro (ConversationPermissionFilterTests). Qui restano solo le regole di business
    // (esistenza, conflitti, "l'Owner non si rimuove") che il service mantiene come fonte di verità.

    [Fact]
    public async Task Get_ReturnsConversation()
    {
        var creator = await SeedUser();
        var conv = await Sut().CreateAsync("c", creator);

        var got = await Sut().GetAsync(conv.Id);
        got.Id.Should().Be(conv.Id);
    }

    [Fact]
    public async Task ListMembers_OnlyMembersOfTheConversation()
    {
        var alice = await SeedUser("alice@x");
        var bob = await SeedUser("bob@x");
        var conv = await Sut().CreateAsync("c", alice);
        await Sut().AddMemberAsync(conv.Id, alice, bob, includeHistory: false);

        var members = await Sut().ListMembersAsync(conv.Id);
        members.Select(m => m.Email).Should().BeEquivalentTo(new[] { "alice@x", "bob@x" });
    }

    [Fact]
    public async Task AddMember_AlreadyMember_ThrowsConflict()
    {
        var owner = await SeedUser("o@x");
        var dup = await SeedUser("d@x");
        var conv = await Sut().CreateAsync("c", owner);
        await Sut().AddMemberAsync(conv.Id, owner, dup, includeHistory: false);

        Func<Task> act = async () => await Sut().AddMemberAsync(conv.Id, owner, dup, false);
        await act.Should().ThrowAsync<ConflictException>();
    }

    [Fact]
    public async Task AddMember_UnknownUser_ThrowsNotFound()
    {
        var owner = await SeedUser();
        var conv = await Sut().CreateAsync("c", owner);

        Func<Task> act = async () => await Sut().AddMemberAsync(conv.Id, owner, Guid.NewGuid(), false);
        await act.Should().ThrowAsync<NotFoundException>();
    }

    // NB: la distribuzione delle chiavi (grant all'add, rotazione al remove, history) è ora
    // client-side (CekProvisioner): il server non crea/copia/ruota più CEK. La copertura del grant
    // vive in CekProvisionerTests; qui restano solo membership/epoch/audit/notify.

    [Fact]
    public async Task RemoveMember_IncrementsEpoch_NotifiesAndAudits()
    {
        var owner = await SeedUser("o@x");
        var target = await SeedUser("t@x");
        var conv = await Sut().CreateAsync("c", owner);
        await Sut().AddMemberAsync(conv.Id, owner, target, false);
        _notifier.Invocations.Clear();

        var newEpoch = await Sut().RemoveMemberAsync(conv.Id, owner, target);

        newEpoch.Should().Be(2);
        (await _db.Conversations.FindAsync(conv.Id))!.CurrentEpochId.Should().Be(2);
        // MemberRemoved si manda via NotifyUsersAsync (audience = membri rimanenti + utente rimosso),
        // mentre l'EpochRotated va al broadcast standard (solo membri attivi).
        _notifier.Verify(
            n => n.NotifyUsersAsync(It.IsAny<IEnumerable<Guid>>(), It.IsAny<MemberChangedEvent>()),
            Times.Once);
        _notifier.Verify(n => n.NotifyAsync(conv.Id, It.IsAny<EpochRotatedEvent>()), Times.Once);
        (await _db.AuditLog.Where(a => a.Action == "MemberRemoved").CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task RemoveMember_ShredsTargetWraps_AllEpochs_KeepsOthers()
    {
        var owner = await SeedUser("o@x");
        var target = await SeedUser("t@x");
        var conv = await Sut().CreateAsync("c", owner);
        await Sut().AddMemberAsync(conv.Id, owner, target, false);

        var ownerDevice = Guid.NewGuid();
        var targetDevice = Guid.NewGuid();
        _db.DevicePublicKeys.Add(new DevicePublicKeyEntity
        {
            UserId = owner, DeviceId = ownerDevice, RegisteredAt = DateTime.UtcNow
        });
        _db.DevicePublicKeys.Add(new DevicePublicKeyEntity
        {
            UserId = target, DeviceId = targetDevice, RegisteredAt = DateTime.UtcNow
        });
        // Wrap per entrambi i device su due epoch: lo shred deve colpire SOLO quelli del target.
        foreach (var epoch in new[] { 1, 2 })
        {
            _db.KeyEnvelopes.Add(new KeyEnvelopeEntity
            {
                ConversationId = conv.Id, EpochId = epoch, DeviceId = ownerDevice, WrappedCek = new byte[32]
            });
            _db.KeyEnvelopes.Add(new KeyEnvelopeEntity
            {
                ConversationId = conv.Id, EpochId = epoch, DeviceId = targetDevice, WrappedCek = new byte[32]
            });
        }
        await _db.SaveChangesAsync();

        await Sut().RemoveMemberAsync(conv.Id, owner, target);

        var wraps = await _db.KeyEnvelopes.Where(k => k.ConversationId == conv.Id).ToListAsync();
        wraps.Should().OnlyContain(k => k.DeviceId == ownerDevice, "the removed member's wraps must be crypto-shredded");
        wraps.Should().HaveCount(2, "owner wraps for both epochs survive");
    }

    [Fact]
    public async Task RemoveMember_UnknownTarget_Throws()
    {
        var owner = await SeedUser();
        var conv = await Sut().CreateAsync("c", owner);

        Func<Task> act = async () => await Sut().RemoveMemberAsync(conv.Id, owner, Guid.NewGuid());
        await act.Should().ThrowAsync<NotFoundException>();
    }

    [Fact]
    public async Task RemoveMember_OwnerCannotBeRemoved_EvenByAdmin()
    {
        // Scenario reale che ha rotto la produzione: il vecchio owner trasferisce la ownership
        // (resta Admin) e poi rimuove il nuovo owner. Dev'essere Forbidden a prescindere dal ruolo
        // del chiamante: l'Owner si rimuove solo passando da un altro TransferOwnership.
        var oldOwner = await SeedUser("old@x");
        var newOwner = await SeedUser("new@x");
        var conv = await Sut().CreateAsync("c", oldOwner);
        await Sut().AddMemberAsync(conv.Id, oldOwner, newOwner, includeHistory: false);
        await Sut().TransferOwnershipAsync(conv.Id, oldOwner, newOwner);
        // oldOwner ora è Admin; newOwner è Owner.

        Func<Task> act = async () => await Sut().RemoveMemberAsync(conv.Id, oldOwner, newOwner);
        await act.Should().ThrowAsync<ForbiddenException>();

        // E nemmeno l'Owner stesso si auto-rimuove.
        Func<Task> selfRemove = async () => await Sut().RemoveMemberAsync(conv.Id, newOwner, newOwner);
        await selfRemove.Should().ThrowAsync<ForbiddenException>();
    }

    [Fact]
    public async Task TransferOwnership_SwapsRoles()
    {
        var owner = await SeedUser("o@x");
        var target = await SeedUser("t@x");
        var conv = await Sut().CreateAsync("c", owner);
        await Sut().AddMemberAsync(conv.Id, owner, target, false);
        _notifier.Invocations.Clear();

        await Sut().TransferOwnershipAsync(conv.Id, owner, target);

        var members = await _db.Members.Where(m => m.RemovedAt == null).ToListAsync();
        members.Single(m => m.UserId == owner).Role.Should().Be("Admin");
        members.Single(m => m.UserId == target).Role.Should().Be("Owner");

        // Senza notifica i client restano fermi sul vecchio pannello: verifichiamo che parta un
        // MemberChanged per ogni utente toccato dal cambio di ruolo.
        _notifier.Verify(
            n => n.NotifyAsync(conv.Id, It.Is<MemberChangedEvent>(e => e.Action == "RoleChanged")),
            Times.Exactly(2));
    }

    [Fact]
    public async Task TransferOwnership_NonMemberTarget_Throws()
    {
        var owner = await SeedUser("o@x");
        var stranger = await SeedUser("s@x");
        var conv = await Sut().CreateAsync("c", owner);

        Func<Task> act = async () => await Sut().TransferOwnershipAsync(conv.Id, owner, stranger);
        await act.Should().ThrowAsync<NotFoundException>();
    }

    [Fact]
    public async Task ChangeRole_PromotesMemberToAdmin_AuditsAndNotifies()
    {
        var owner = await SeedUser("o@x");
        var member = await SeedUser("m@x");
        var conv = await Sut().CreateAsync("c", owner);
        await Sut().AddMemberAsync(conv.Id, owner, member, false);
        _notifier.Invocations.Clear();

        await Sut().ChangeRoleAsync(conv.Id, owner, member, "Admin");

        (await _db.Members.SingleAsync(m => m.UserId == member && m.RemovedAt == null))
            .Role.Should().Be("Admin");
        (await _db.AuditLog.CountAsync(a => a.Action == "RoleChanged")).Should().Be(1);
        _notifier.Verify(
            n => n.NotifyAsync(conv.Id, It.Is<MemberChangedEvent>(e => e.Action == "RoleChanged")),
            Times.Once);
    }

    [Fact]
    public async Task ChangeRole_OwnerTarget_Throws()
    {
        var owner = await SeedUser("o@x");
        var conv = await Sut().CreateAsync("c", owner);

        Func<Task> act = async () => await Sut().ChangeRoleAsync(conv.Id, owner, owner, "Member");
        await act.Should().ThrowAsync<ForbiddenException>();
    }

    [Fact]
    public async Task ChangeRole_InvalidRole_Throws()
    {
        var owner = await SeedUser("o@x");
        var member = await SeedUser("m@x");
        var conv = await Sut().CreateAsync("c", owner);
        await Sut().AddMemberAsync(conv.Id, owner, member, false);

        Func<Task> act = async () => await Sut().ChangeRoleAsync(conv.Id, owner, member, "Superuser");
        // Ruolo non valido = errore di validazione argomento (mappa 400), NON conflitto di stato (409).
        await act.Should().ThrowAsync<ValidationException>();
    }

    [Fact]
    public async Task Rename_UpdatesName_AuditsAndNotifies()
    {
        var owner = await SeedUser("o@x");
        var conv = await Sut().CreateAsync("Old name", owner);
        _notifier.Invocations.Clear();

        await Sut().RenameAsync(conv.Id, owner, "  New name  ");

        (await _db.Conversations.FindAsync(conv.Id))!.Name.Should().Be("New name");
        (await _db.AuditLog.CountAsync(a => a.Action == "ConversationRenamed")).Should().Be(1);
        _notifier.Verify(
            n => n.NotifyAsync(conv.Id, It.Is<ConversationChangedEvent>(e => e.ChangeType == "Renamed" && e.Name == "New name")),
            Times.Once);
    }

    [Fact]
    public async Task Rename_EmptyName_Throws()
    {
        var owner = await SeedUser("o@x");
        var conv = await Sut().CreateAsync("c", owner);

        Func<Task> act = async () => await Sut().RenameAsync(conv.Id, owner, "   ");
        // Nome vuoto = errore di validazione argomento (mappa 400), NON conflitto di stato (409).
        await act.Should().ThrowAsync<ValidationException>();
    }

    [Fact]
    public async Task Delete_CryptoShredsConversationData_KeepsAudit_AndNotifies()
    {
        var owner = await SeedUser("o@x");
        var member = await SeedUser("m@x");
        var conv = await Sut().CreateAsync("c", owner);
        await Sut().AddMemberAsync(conv.Id, owner, member, false);
        _notifier.Invocations.Clear();

        await Sut().DeleteAsync(conv.Id, owner);

        // Crypto-shred: niente più conversazione, membri, key envelope, contatore di sequenza.
        (await _db.Conversations.CountAsync(c => c.Id == conv.Id)).Should().Be(0);
        (await _db.Members.CountAsync(m => m.ConversationId == conv.Id)).Should().Be(0);
        (await _db.KeyEnvelopes.CountAsync(k => k.ConversationId == conv.Id)).Should().Be(0);
        (await _db.SeqCounters.CountAsync(s => s.ConversationId == conv.Id)).Should().Be(0);

        // Il log di audit (append-only) resta e registra la cancellazione.
        (await _db.AuditLog.CountAsync(a => a.Action == "ConversationDeleted")).Should().Be(1);

        // I membri (owner + member) vengono avvisati così la sidebar toglie la conversazione.
        _notifier.Verify(
            n => n.NotifyUsersAsync(
                It.Is<IEnumerable<Guid>>(ids => ids.Contains(owner) && ids.Contains(member)),
                It.Is<ConversationChangedEvent>(e => e.ChangeType == "Deleted")),
            Times.Once);
    }

    [Fact]
    public async Task Delete_UnknownConversation_Throws()
    {
        var owner = await SeedUser("o@x");
        await Sut().CreateAsync("c", owner);

        Func<Task> act = async () => await Sut().DeleteAsync(Guid.NewGuid(), owner);
        await act.Should().ThrowAsync<NotFoundException>();
    }
}
