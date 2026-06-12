using ECHAT.Models.Dtos;
using ECHAT.Models.Enums;
using ECHAT.Models.Events;
using ECHAT.Server.Core.Exceptions;
using ECHAT.Server.Core.Interfaces;
using ECHAT.Server.Core.Services;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace ECHAT.Server.Core.Tests;

/// <summary>
/// Behavioural tests for <see cref="MigrationOrchestratorService"/>: single-flight lock,
/// FullReencrypt-only start gate, checkpoint/cancel/finalize idempotency,
/// the IDOR job/conversation guard, the FullReencrypt safety check + force bypass, and the
/// crypto-shred + ChainBoundary side-effects on finalize.
/// </summary>
public class MigrationOrchestratorServiceTests
{
    private readonly Mock<IMigrationJobStore> _jobs = new();
    private readonly Mock<IMessageRepository> _messages = new();
    private readonly Mock<IConversationReader> _conversations = new();
    private readonly Mock<IKeyEnvelopeStore> _keyStore = new();
    private readonly Mock<IRealtimeNotifier> _notifier = new();
    private readonly Mock<IChainBoundaryStore> _chains = new();

    private readonly Guid _conv = Guid.NewGuid();
    private readonly Guid _custodian = Guid.NewGuid();

    private MigrationOrchestratorService Sut(bool withChains = true)
        => new(_jobs.Object, _messages.Object, _conversations.Object, _keyStore.Object,
               _notifier.Object, NullLogger<MigrationOrchestratorService>.Instance,
               chainBoundaries: withChains ? _chains.Object : null);

    private MigrationJobRecord JobFor(Guid conv, string status = "InProgress", string mode = "FullReencrypt", long maxReplaced = 0)
        => new()
        {
            Id = Guid.NewGuid(),
            ConversationId = conv,
            Mode = mode,
            Status = status,
            MaxReplacedSeq = maxReplaced,
            CreatedAt = DateTime.UtcNow,
            CustodianUserId = _custodian
        };

    // ---------------- StartMigration ----------------

    [Fact]
    public async Task Start_WhenActiveJobExists_ThrowsConflict_AndCreatesNothing()
    {
        _jobs.Setup(j => j.HasActiveJobAsync(_conv)).ReturnsAsync(true);

        Func<Task> act = () => Sut().StartMigrationAsync(_conv, MigrationMode.FullReencrypt, _custodian);

        await act.Should().ThrowAsync<ConflictException>();
        _jobs.Verify(j => j.CreateAsync(It.IsAny<MigrationJobRecord>()), Times.Never);
    }

    [Fact]
    public async Task Start_RewrapOnly_ThrowsValidation_AndCreatesNothing()
    {
        // RewrapOnly non ha lavoro server-side (epoch bump + shred avvengono in RemoveMember,
        // la nuova CEK è wrappata client-side): un job qui resterebbe InProgress per sempre
        // e bloccherebbe il single-flight. Lo start lo rifiuta.
        Func<Task> act = () => Sut().StartMigrationAsync(_conv, MigrationMode.RewrapOnly, _custodian);

        await act.Should().ThrowAsync<ValidationException>();
        _jobs.Verify(j => j.HasActiveJobAsync(It.IsAny<Guid>()), Times.Never);
        _jobs.Verify(j => j.CreateAsync(It.IsAny<MigrationJobRecord>()), Times.Never);
    }

    [Fact]
    public async Task Start_FullReencrypt_CreatesInProgressJob_AndStaysClientDriven()
    {
        _jobs.Setup(j => j.HasActiveJobAsync(_conv)).ReturnsAsync(false);

        await Sut().StartMigrationAsync(_conv, MigrationMode.FullReencrypt, _custodian);

        _jobs.Verify(j => j.CreateAsync(It.Is<MigrationJobRecord>(
            r => r.ConversationId == _conv && r.Mode == "FullReencrypt"
                 && r.Status == "InProgress" && r.CustodianUserId == _custodian)), Times.Once);
        // Initial 0% progress notify fired.
        _notifier.Verify(n => n.NotifyAsync(_conv, It.IsAny<RealtimeEvent>()), Times.AtLeastOnce);
        // FullReencrypt is client-driven: no inline batch, no completion save.
        _conversations.Verify(c => c.GetCurrentEpochAsync(It.IsAny<Guid>()), Times.Never);
        _jobs.Verify(j => j.SaveAsync(It.IsAny<MigrationJobRecord>()), Times.Never);
    }

    // ---------------- Checkpoint ----------------

    [Fact]
    public async Task Checkpoint_AdvancesProgress_AndNotifies()
    {
        var job = JobFor(_conv);
        _jobs.Setup(j => j.GetByIdAsync(job.Id)).ReturnsAsync(job);

        await Sut().CheckpointAsync(_conv, job.Id, batchId: 3, progressPercent: 60);

        _jobs.Verify(j => j.SaveAsync(It.Is<MigrationJobRecord>(
            r => r.Status == "InProgress" && r.LastCheckpointBatchId == 3 && r.ProgressPercent == 60)), Times.Once);
        _notifier.Verify(n => n.NotifyAsync(_conv,
            It.Is<JobProgressEvent>(e => e.Status == "InProgress" && e.ProgressPercent == 60)), Times.Once);
    }

    [Fact]
    public async Task Checkpoint_TerminalJob_IsIgnored_NoSaveNoNotify()
    {
        var job = JobFor(_conv, status: "Completed");
        _jobs.Setup(j => j.GetByIdAsync(job.Id)).ReturnsAsync(job);

        await Sut().CheckpointAsync(_conv, job.Id, 9, 90);

        _jobs.Verify(j => j.SaveAsync(It.IsAny<MigrationJobRecord>()), Times.Never);
        _notifier.Verify(n => n.NotifyAsync(It.IsAny<Guid>(), It.IsAny<RealtimeEvent>()), Times.Never);
    }

    [Fact]
    public async Task Checkpoint_SaveLosesRace_NoNotify()
    {
        var job = JobFor(_conv);
        _jobs.Setup(j => j.GetByIdAsync(job.Id)).ReturnsAsync(job);
        _jobs.Setup(j => j.SaveAsync(It.IsAny<MigrationJobRecord>()))
            .ThrowsAsync(new ConcurrencyConflictException());

        await Sut().CheckpointAsync(_conv, job.Id, 1, 10);

        _notifier.Verify(n => n.NotifyAsync(It.IsAny<Guid>(), It.IsAny<RealtimeEvent>()), Times.Never);
    }

    // ---------------- IDOR guard (LoadJobForConversation) ----------------

    [Fact]
    public async Task Checkpoint_UnknownJob_ThrowsNotFound()
    {
        var jobId = Guid.NewGuid();
        _jobs.Setup(j => j.GetByIdAsync(jobId)).ReturnsAsync((MigrationJobRecord?)null);

        Func<Task> act = () => Sut().CheckpointAsync(_conv, jobId, 1, 1);

        await act.Should().ThrowAsync<NotFoundException>();
    }

    [Fact]
    public async Task Finalize_JobBelongsToAnotherConversation_ThrowsNotFound_NoShred()
    {
        // IDOR: admin of _conv passes a jobId owned by a different conversation.
        var foreignJob = JobFor(Guid.NewGuid());
        _jobs.Setup(j => j.GetByIdAsync(foreignJob.Id)).ReturnsAsync(foreignJob);

        Func<Task> act = () => Sut().FinalizeAsync(_conv, foreignJob.Id);

        await act.Should().ThrowAsync<NotFoundException>();
        _keyStore.Verify(k => k.DeleteWrapsAsync(It.IsAny<Guid>(), It.IsAny<int>(), It.IsAny<Guid?>()), Times.Never);
    }

    // ---------------- Cancel ----------------

    [Fact]
    public async Task Cancel_MovesJobToCancelled_AndNotifies()
    {
        var job = JobFor(_conv);
        job.ProgressPercent = 42;
        _jobs.Setup(j => j.GetByIdAsync(job.Id)).ReturnsAsync(job);

        await Sut().CancelAsync(_conv, job.Id);

        _jobs.Verify(j => j.SaveAsync(It.Is<MigrationJobRecord>(
            r => r.Status == "Cancelled" && r.CompletedAt != null)), Times.Once);
        _notifier.Verify(n => n.NotifyAsync(_conv,
            It.Is<JobProgressEvent>(e => e.Status == "Cancelled" && e.ProgressPercent == 42)), Times.Once);
    }

    [Fact]
    public async Task Cancel_AlreadyTerminal_IsIdempotent_NoSave()
    {
        var job = JobFor(_conv, status: "Cancelled");
        _jobs.Setup(j => j.GetByIdAsync(job.Id)).ReturnsAsync(job);

        await Sut().CancelAsync(_conv, job.Id);

        _jobs.Verify(j => j.SaveAsync(It.IsAny<MigrationJobRecord>()), Times.Never);
        _notifier.Verify(n => n.NotifyAsync(It.IsAny<Guid>(), It.IsAny<RealtimeEvent>()), Times.Never);
    }

    // ---------------- Finalize ----------------

    [Fact]
    public async Task Finalize_AlreadyTerminal_ReturnsWithoutShred()
    {
        var job = JobFor(_conv, status: "Completed", mode: nameof(MigrationMode.FullReencrypt));
        _jobs.Setup(j => j.GetByIdAsync(job.Id)).ReturnsAsync(job);

        await Sut().FinalizeAsync(_conv, job.Id);

        _conversations.Verify(c => c.GetCurrentEpochAsync(It.IsAny<Guid>()), Times.Never);
        _keyStore.Verify(k => k.DeleteWrapsAsync(It.IsAny<Guid>(), It.IsAny<int>(), It.IsAny<Guid?>()), Times.Never);
    }

    [Fact]
    public async Task Finalize_ConversationMissing_Throws()
    {
        var job = JobFor(_conv, mode: nameof(MigrationMode.FullReencrypt));
        _jobs.Setup(j => j.GetByIdAsync(job.Id)).ReturnsAsync(job);
        _conversations.Setup(c => c.GetCurrentEpochAsync(_conv)).ReturnsAsync((int?)null);

        Func<Task> act = () => Sut().FinalizeAsync(_conv, job.Id);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task Finalize_FullReencrypt_WithRemainingOldEpochEnvelopes_RefusesWithConflict_NoShred()
    {
        var job = JobFor(_conv, mode: nameof(MigrationMode.FullReencrypt));
        _jobs.Setup(j => j.GetByIdAsync(job.Id)).ReturnsAsync(job);
        _conversations.Setup(c => c.GetCurrentEpochAsync(_conv)).ReturnsAsync(7);
        _messages.Setup(m => m.CountByEpochBelowAsync(_conv, 7)).ReturnsAsync(3); // not all re-encrypted

        Func<Task> act = () => Sut().FinalizeAsync(_conv, job.Id);

        await act.Should().ThrowAsync<ConflictException>();
        _keyStore.Verify(k => k.DeleteWrapsAsync(It.IsAny<Guid>(), It.IsAny<int>(), It.IsAny<Guid?>()), Times.Never);
    }

    [Fact]
    public async Task Finalize_FullReencrypt_AllReencrypted_ShredsOldEpochs_AndCompletes()
    {
        var job = JobFor(_conv, mode: nameof(MigrationMode.FullReencrypt), maxReplaced: 0);
        _jobs.Setup(j => j.GetByIdAsync(job.Id)).ReturnsAsync(job);
        _conversations.Setup(c => c.GetCurrentEpochAsync(_conv)).ReturnsAsync(3);
        _messages.Setup(m => m.CountByEpochBelowAsync(_conv, 3)).ReturnsAsync(0); // safety check passes
        _keyStore.Setup(k => k.GetKeysAsync(_conv, null, null)).ReturnsAsync(new List<WrappedKey>
        {
            new() { ConversationId = _conv, EpochId = 1, DeviceId = Guid.NewGuid() },
            new() { ConversationId = _conv, EpochId = 1, DeviceId = Guid.NewGuid() }, // dup epoch -> distinct
            new() { ConversationId = _conv, EpochId = 2, DeviceId = Guid.NewGuid() },
            new() { ConversationId = _conv, EpochId = 3, DeviceId = Guid.NewGuid() }, // current epoch -> kept
        });

        await Sut().FinalizeAsync(_conv, job.Id);

        // Only epochs 1 and 2 (< current 3) are shredded, once each, deviceId null (all devices).
        _keyStore.Verify(k => k.DeleteWrapsAsync(_conv, 1, null), Times.Once);
        _keyStore.Verify(k => k.DeleteWrapsAsync(_conv, 2, null), Times.Once);
        _keyStore.Verify(k => k.DeleteWrapsAsync(_conv, 3, It.IsAny<Guid?>()), Times.Never);
        _jobs.Verify(j => j.SaveAsync(It.Is<MigrationJobRecord>(
            r => r.Status == "Completed" && r.ProgressPercent == 100 && r.CompletedAt != null)), Times.Once);
        // No replacements recorded => no chain boundary.
        _chains.Verify(c => c.AddAsync(It.IsAny<Guid>(), It.IsAny<long>(), It.IsAny<int>()), Times.Never);
    }

    [Fact]
    public async Task Finalize_FullReencrypt_WithReplacements_WritesChainBoundary()
    {
        var job = JobFor(_conv, mode: nameof(MigrationMode.FullReencrypt), maxReplaced: 42);
        _jobs.Setup(j => j.GetByIdAsync(job.Id)).ReturnsAsync(job);
        _conversations.Setup(c => c.GetCurrentEpochAsync(_conv)).ReturnsAsync(4);
        _messages.Setup(m => m.CountByEpochBelowAsync(_conv, 4)).ReturnsAsync(0);
        _keyStore.Setup(k => k.GetKeysAsync(_conv, null, null)).ReturnsAsync(new List<WrappedKey>());

        await Sut().FinalizeAsync(_conv, job.Id);

        _chains.Verify(c => c.AddAsync(_conv, 42, 4), Times.Once);
    }

    [Fact]
    public async Task Finalize_FullReencrypt_WithReplacements_ButNoChainStore_DoesNotThrow()
    {
        var job = JobFor(_conv, mode: nameof(MigrationMode.FullReencrypt), maxReplaced: 10);
        _jobs.Setup(j => j.GetByIdAsync(job.Id)).ReturnsAsync(job);
        _conversations.Setup(c => c.GetCurrentEpochAsync(_conv)).ReturnsAsync(4);
        _messages.Setup(m => m.CountByEpochBelowAsync(_conv, 4)).ReturnsAsync(0);
        _keyStore.Setup(k => k.GetKeysAsync(_conv, null, null)).ReturnsAsync(new List<WrappedKey>());

        Func<Task> act = () => Sut(withChains: false).FinalizeAsync(_conv, job.Id);

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task ForceFinalize_FullReencrypt_BypassesSafetyCheck_ShredsEvenWithRemaining()
    {
        var job = JobFor(_conv, mode: nameof(MigrationMode.FullReencrypt));
        _jobs.Setup(j => j.GetByIdAsync(job.Id)).ReturnsAsync(job);
        _conversations.Setup(c => c.GetCurrentEpochAsync(_conv)).ReturnsAsync(5);
        // Remaining envelopes at old epoch: Finalize would refuse, ForceFinalize accepts data loss.
        _messages.Setup(m => m.CountByEpochBelowAsync(_conv, 5)).ReturnsAsync(8);
        _keyStore.Setup(k => k.GetKeysAsync(_conv, null, null)).ReturnsAsync(new List<WrappedKey>
        {
            new() { ConversationId = _conv, EpochId = 4, DeviceId = Guid.NewGuid() },
        });

        await Sut().ForceFinalizeAsync(_conv, job.Id);

        _keyStore.Verify(k => k.DeleteWrapsAsync(_conv, 4, null), Times.Once);
        _jobs.Verify(j => j.SaveAsync(It.Is<MigrationJobRecord>(r => r.Status == "Completed")), Times.Once);
    }

    [Fact]
    public async Task Finalize_LegacyRewrapOnlyJob_SkipsSafetyCheck_AndStillShreds()
    {
        // StartMigration non crea più job RewrapOnly, ma record legacy possono esistere nel DB:
        // niente CountByEpochBelow safety gate per quel mode, lo shred però gira comunque.
        var job = JobFor(_conv, mode: nameof(MigrationMode.RewrapOnly));
        _jobs.Setup(j => j.GetByIdAsync(job.Id)).ReturnsAsync(job);
        _conversations.Setup(c => c.GetCurrentEpochAsync(_conv)).ReturnsAsync(2);
        _keyStore.Setup(k => k.GetKeysAsync(_conv, null, null)).ReturnsAsync(new List<WrappedKey>
        {
            new() { ConversationId = _conv, EpochId = 1, DeviceId = Guid.NewGuid() },
        });

        await Sut().FinalizeAsync(_conv, job.Id);

        _keyStore.Verify(k => k.DeleteWrapsAsync(_conv, 1, null), Times.Once);
        _jobs.Verify(j => j.SaveAsync(It.Is<MigrationJobRecord>(r => r.Status == "Completed")), Times.Once);
        // Chain boundary only applies to FullReencrypt.
        _chains.Verify(c => c.AddAsync(It.IsAny<Guid>(), It.IsAny<long>(), It.IsAny<int>()), Times.Never);
    }

    // ---------------- Thin pass-throughs (real delegation, not getters) ----------------

    [Fact]
    public async Task RecordReplacement_DelegatesToStore()
    {
        var jobId = Guid.NewGuid();
        await Sut().RecordReplacementAsync(jobId, 99);
        _jobs.Verify(j => j.UpdateMaxReplacedSeqAsync(jobId, 99), Times.Once);
    }

    [Fact]
    public async Task GetActiveFullReencryptJob_DelegatesToStore()
    {
        var expected = JobFor(_conv, mode: nameof(MigrationMode.FullReencrypt));
        _jobs.Setup(j => j.GetActiveFullReencryptJobAsync(_conv)).ReturnsAsync(expected);

        var result = await Sut().GetActiveFullReencryptJobAsync(_conv);

        result.Should().BeSameAs(expected);
    }
}
