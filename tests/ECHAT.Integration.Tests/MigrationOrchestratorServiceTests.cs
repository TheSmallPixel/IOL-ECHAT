using ECHAT.Models.Dtos;
using ECHAT.Models.Enums;
using ECHAT.Models.Events;
using ECHAT.Server.App.Data;
using ECHAT.Server.App.Data.Entities;
using ECHAT.Server.App.Repositories;
using ECHAT.Server.Core.Interfaces;
using ECHAT.Server.Core.Services;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Moq;

namespace ECHAT.Integration.Tests;

public class MigrationOrchestratorServiceTests : IDisposable
{
    private readonly EchatDbContext _db;
    private readonly KeyEnvelopeRepository _keys;
    private readonly Mock<IRealtimeNotifier> _notifier = new();
    private readonly Guid _custodian = Guid.NewGuid();

    public MigrationOrchestratorServiceTests()
    {
        var options = new DbContextOptionsBuilder<EchatDbContext>()
            .UseInMemoryDatabase($"echat-{Guid.NewGuid()}")
            .Options;
        _db = new EchatDbContext(options);
        _keys = new KeyEnvelopeRepository(_db);
    }

    public void Dispose() => _db.Dispose();

    private MigrationOrchestratorService Sut() => new(
        new MigrationJobStore(_db),
        new MessageRepository(_db),
        new ConversationReader(_db),
        _keys,
        _notifier.Object);

    private async Task<Guid> SeedConversation(int currentEpoch, int messageCount)
    {
        var conversationId = Guid.NewGuid();
        _db.Conversations.Add(new ConversationEntity
        {
            Id = conversationId,
            Name = "test",
            CurrentEpochId = currentEpoch,
            CreatedAt = DateTime.UtcNow
        });

        for (int i = 1; i <= messageCount; i++)
        {
            _db.Messages.Add(new MessageEntity
            {
                ConversationId = conversationId,
                MessageId = Guid.NewGuid(),
                Seq = i,
                EpochId = currentEpoch - 1,
                Type = MessageType.Text
            });
        }

        await _db.SaveChangesAsync();
        return conversationId;
    }

    [Fact]
    public async Task Start_RewrapOnly_IsRejected_AndCreatesNoJob()
    {
        // RewrapOnly non ha lavoro server-side: epoch bump + shred avvengono in RemoveMember,
        // la nuova CEK è wrappata client-side. Un job qui resterebbe InProgress per sempre.
        var conversationId = await SeedConversation(currentEpoch: 2, messageCount: 250);

        var act = async () => await Sut().StartMigrationAsync(conversationId, MigrationMode.RewrapOnly, _custodian);

        await act.Should().ThrowAsync<ECHAT.Server.Core.Exceptions.ValidationException>()
            .WithMessage("*FullReencrypt*");
        (await _db.MigrationJobs.AnyAsync()).Should().BeFalse();
    }

    [Fact]
    public async Task Start_RejectsConcurrent_Job()
    {
        var conversationId = await SeedConversation(currentEpoch: 2, messageCount: 50);

        await Sut().StartMigrationAsync(conversationId, MigrationMode.FullReencrypt, _custodian);

        // Il primo job è InProgress (custodian-driven), quindi un secondo start deve essere rifiutato.
        var act = async () => await Sut().StartMigrationAsync(conversationId, MigrationMode.FullReencrypt, _custodian);
        await act.Should().ThrowAsync<ECHAT.Server.Core.Exceptions.ConflictException>().WithMessage("*already active*");
    }

    [Fact]
    public async Task Checkpoint_UpdatesJob_AndNotifies()
    {
        var conversationId = await SeedConversation(currentEpoch: 2, messageCount: 100);
        var jobId = await Sut().StartMigrationAsync(conversationId, MigrationMode.FullReencrypt, _custodian);
        _notifier.Invocations.Clear();

        await Sut().CheckpointAsync(conversationId, jobId, batchId: 42, progressPercent: 60);

        var job = await _db.MigrationJobs.FindAsync(jobId);
        job!.LastCheckpointBatchId.Should().Be(42);
        job.ProgressPercent.Should().Be(60);
        _notifier.Verify(n => n.NotifyAsync(
            conversationId,
            It.Is<JobProgressEvent>(e => e.ProgressPercent == 60 && e.Status == "InProgress")), Times.Once);
    }

    [Fact]
    public async Task Finalize_DeletesOldEpochWraps_AndMarksCompleted()
    {
        // messageCount = 0: per FullReencrypt il finalize rifiuta finché restano envelope a
        // epoch < current (safety check). Qui verifichiamo il path canonico
        // "InProgress -> Finalize fa crypto-shred + marca Completed".
        var conversationId = await SeedConversation(currentEpoch: 3, messageCount: 0);
        var alice = Guid.NewGuid();

        // Due epoch vecchie di wrap per Alice più una corrente
        await _keys.StoreWrapsAsync(conversationId, new List<WrappedKey>
        {
            new() { ConversationId = conversationId, EpochId = 1, DeviceId = alice, WrappedCek = new byte[32] },
            new() { ConversationId = conversationId, EpochId = 2, DeviceId = alice, WrappedCek = new byte[32] },
            new() { ConversationId = conversationId, EpochId = 3, DeviceId = alice, WrappedCek = new byte[32] },
        });

        var jobId = await Sut().StartMigrationAsync(conversationId, MigrationMode.FullReencrypt, _custodian);
        await Sut().FinalizeAsync(conversationId, jobId);

        var remaining = await _keys.GetKeysAsync(conversationId, epochId: null, deviceId: alice);
        remaining.Should().ContainSingle()
            .Which.EpochId.Should().Be(3, "old epochs must be crypto-shredded");

        var job = await _db.MigrationJobs.FindAsync(jobId);
        job!.Status.Should().Be("Completed");
        job.ProgressPercent.Should().Be(100);
    }

    [Fact]
    public async Task Checkpoint_UnknownJob_Throws()
    {
        var act = async () => await Sut().CheckpointAsync(Guid.NewGuid(), Guid.NewGuid(), 1, 50);
        await act.Should().ThrowAsync<ECHAT.Server.Core.Exceptions.NotFoundException>().WithMessage("*not found*");
    }

    [Fact]
    public async Task Finalize_UnknownJob_Throws()
    {
        var act = async () => await Sut().FinalizeAsync(Guid.NewGuid(), Guid.NewGuid());
        await act.Should().ThrowAsync<ECHAT.Server.Core.Exceptions.NotFoundException>().WithMessage("*not found*");
    }

    [Fact]
    public async Task Checkpoint_CrossConversationJobId_Rejected()
    {
        // IDOR guard: il job appartiene a conversationA ma il chiamante è admin di conversationB
        // e passa il jobId di A sulla route di B. Deve essere rifiutato come 404.
        var conversationA = await SeedConversation(currentEpoch: 2, messageCount: 100);
        var conversationB = await SeedConversation(currentEpoch: 2, messageCount: 100);
        var jobId = await Sut().StartMigrationAsync(conversationA, MigrationMode.FullReencrypt, _custodian);

        var act = async () => await Sut().CheckpointAsync(conversationB, jobId, batchId: 1, progressPercent: 50);
        await act.Should().ThrowAsync<ECHAT.Server.Core.Exceptions.NotFoundException>().WithMessage("*not found*");
    }

    [Fact]
    public async Task Finalize_CrossConversationJobId_Rejected()
    {
        var conversationA = await SeedConversation(currentEpoch: 2, messageCount: 100);
        var conversationB = await SeedConversation(currentEpoch: 2, messageCount: 100);
        var jobId = await Sut().StartMigrationAsync(conversationA, MigrationMode.FullReencrypt, _custodian);

        var act = async () => await Sut().FinalizeAsync(conversationB, jobId);
        await act.Should().ThrowAsync<ECHAT.Server.Core.Exceptions.NotFoundException>().WithMessage("*not found*");

        // Il job di A NON deve essere stato toccato (niente crypto-shred su conversazione altrui).
        var jobA = await _db.MigrationJobs.FindAsync(jobId);
        jobA!.Status.Should().Be("InProgress");
    }

    [Fact]
    public async Task ForceFinalize_CrossConversationJobId_Rejected()
    {
        var conversationA = await SeedConversation(currentEpoch: 2, messageCount: 100);
        var conversationB = await SeedConversation(currentEpoch: 2, messageCount: 100);
        var jobId = await Sut().StartMigrationAsync(conversationA, MigrationMode.FullReencrypt, _custodian);

        var act = async () => await Sut().ForceFinalizeAsync(conversationB, jobId);
        await act.Should().ThrowAsync<ECHAT.Server.Core.Exceptions.NotFoundException>().WithMessage("*not found*");

        var jobA = await _db.MigrationJobs.FindAsync(jobId);
        jobA!.Status.Should().Be("InProgress");
    }

    [Fact]
    public async Task Cancel_CrossConversationJobId_Rejected()
    {
        var conversationA = await SeedConversation(currentEpoch: 2, messageCount: 100);
        var conversationB = await SeedConversation(currentEpoch: 2, messageCount: 100);
        var jobId = await Sut().StartMigrationAsync(conversationA, MigrationMode.FullReencrypt, _custodian);

        var act = async () => await Sut().CancelAsync(conversationB, jobId);
        await act.Should().ThrowAsync<ECHAT.Server.Core.Exceptions.NotFoundException>().WithMessage("*not found*");

        var jobA = await _db.MigrationJobs.FindAsync(jobId);
        jobA!.Status.Should().Be("InProgress");
    }

    [Fact]
    public async Task FullReencrypt_DoesNotAutoAdvance_LeavesJobForCustodian()
    {
        var conversationId = await SeedConversation(currentEpoch: 2, messageCount: 250);

        var jobId = await Sut().StartMigrationAsync(conversationId, MigrationMode.FullReencrypt, _custodian);

        var job = await _db.MigrationJobs.FindAsync(jobId);
        job.Should().NotBeNull();
        job!.Status.Should().Be("InProgress", "FullReencrypt is custodian-driven; the server must not auto-finish it");
        job.ProgressPercent.Should().Be(0);
        job.LastCheckpointBatchId.Should().BeNull("the server never invoked the batch loop");
        // Deve essere partita solo la notifica iniziale di Start a 0%.
        _notifier.Verify(n => n.NotifyAsync(conversationId, It.IsAny<JobProgressEvent>()), Times.Once);
    }
}
