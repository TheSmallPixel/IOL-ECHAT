using ECHAT.Models.Domain;
using ECHAT.Models.Enums;
using ECHAT.Server.Core.Interfaces;
using ECHAT.Server.Core.Pipeline;
using ECHAT.Server.Core.Services;
using FluentAssertions;
using Moq;

namespace ECHAT.Server.Core.Tests;

public class MessageIngestPipelineTests
{
    private readonly Mock<ISequenceService> _seqService = new();
    private readonly Mock<IMessageRepository> _messageRepo = new();
    private readonly Mock<IRealtimeNotifier> _notifier = new();
    private readonly Mock<ISeqCounterStore> _counterStore = new();
    private readonly Mock<IAuditLog> _audit = new();

    public MessageIngestPipelineTests()
    {
        _counterStore
            .Setup(s => s.GetAsync(It.IsAny<Guid>()))
            .ReturnsAsync((Guid id) => new SeqCounter { ConversationId = id, NextSeq = 1, AnchorSeq = 0, AnchorEnvelopeHash = Array.Empty<byte>() });
    }

    private MessageEnvelope CreateEnvelope(long seq = 1) => new()
    {
        ConversationId = Guid.NewGuid(),
        MessageId = Guid.NewGuid(),
        Seq = seq,
        EpochId = 1,
        SenderDeviceId = Guid.NewGuid(),
        Nonce = new byte[] { 1 },
        Ciphertext = new byte[] { 2 },
        Signature = new byte[] { 3 },
        LeaseToken = "valid-token",
        Type = MessageType.Text
    };

    private IIngestHandler[] BuildHandlers() => new IIngestHandler[]
    {
        new LeaseValidationHandler(_seqService.Object),
        new DeduplicationHandler(_messageRepo.Object),
        new PersistHandler(_messageRepo.Object, _counterStore.Object),
        new NotifyHandler(_notifier.Object),
        new AuditHandler(_audit.Object)
    };

    [Fact]
    public async Task IngestAsync_ValidMessage_ShouldPersistAndNotify()
    {
        _seqService.Setup(s => s.ValidateLeaseAsync(It.IsAny<Guid>(), It.IsAny<long>(), It.IsAny<string>()))
            .ReturnsAsync(true);
        _messageRepo.Setup(r => r.ExistsAsync(It.IsAny<Guid>()))
            .ReturnsAsync(false);

        var pipeline = new MessageIngestPipeline(BuildHandlers());
        var envelope = CreateEnvelope();

        var ack = await pipeline.IngestAsync(envelope, envelope.SenderDeviceId);

        ack.Should().NotBeNull();
        ack.Seq.Should().Be(envelope.Seq);
        _messageRepo.Verify(r => r.AppendAsync(envelope), Times.Once);
        _notifier.Verify(n => n.NotifyAsync(envelope.ConversationId, It.IsAny<Models.Events.RealtimeEvent>()), Times.Once);
        _counterStore.Verify(c => c.UpdateAnchorAsync(envelope.ConversationId, envelope.Seq, It.IsAny<byte[]>()), Times.Once);
        _audit.Verify(a => a.RecordAsync(It.IsAny<Models.Dtos.AuditEntry>()), Times.Once);
    }

    [Fact]
    public async Task IngestAsync_DuplicateMessage_ShouldReturnAckWithoutPersist()
    {
        _seqService.Setup(s => s.ValidateLeaseAsync(It.IsAny<Guid>(), It.IsAny<long>(), It.IsAny<string>()))
            .ReturnsAsync(true);
        _messageRepo.Setup(r => r.ExistsAsync(It.IsAny<Guid>()))
            .ReturnsAsync(true);

        var pipeline = new MessageIngestPipeline(BuildHandlers());
        var envelope = CreateEnvelope();

        var ack = await pipeline.IngestAsync(envelope, envelope.SenderDeviceId);

        ack.Should().NotBeNull();
        _messageRepo.Verify(r => r.AppendAsync(It.IsAny<MessageEnvelope>()), Times.Never);
        _notifier.Verify(n => n.NotifyAsync(It.IsAny<Guid>(), It.IsAny<Models.Events.RealtimeEvent>()), Times.Never);
        _audit.Verify(a => a.RecordAsync(It.IsAny<Models.Dtos.AuditEntry>()), Times.Never);
    }

    [Fact]
    public async Task IngestAsync_InvalidLease_ShouldThrow()
    {
        _seqService.Setup(s => s.ValidateLeaseAsync(It.IsAny<Guid>(), It.IsAny<long>(), It.IsAny<string>()))
            .ReturnsAsync(false);

        var pipeline = new MessageIngestPipeline(BuildHandlers());
        var envelope = CreateEnvelope();

        var act = async () => await pipeline.IngestAsync(envelope, envelope.SenderDeviceId);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*Invalid lease*");
    }

    [Fact]
    public async Task IngestAsync_OutOfOrderSeq_StillPersists()
    {
        // Mittenti diversi con lease distinti possono arrivare in qualunque ordine: è il punto
        // di avere un seq ordinabile globalmente invece dell'ordine di arrivo. La pipeline deve
        // accettare un seq basso anche se uno più alto è già stato persistito.
        _seqService.Setup(s => s.ValidateLeaseAsync(It.IsAny<Guid>(), It.IsAny<long>(), It.IsAny<string>()))
            .ReturnsAsync(true);
        _messageRepo.Setup(r => r.ExistsAsync(It.IsAny<Guid>())).ReturnsAsync(false);
        _counterStore
            .Setup(c => c.GetAsync(It.IsAny<Guid>()))
            .ReturnsAsync((Guid id) => new SeqCounter { ConversationId = id, AnchorSeq = 50 });

        var pipeline = new MessageIngestPipeline(BuildHandlers());
        var envelope = CreateEnvelope(seq: 3);

        var ack = await pipeline.IngestAsync(envelope, envelope.SenderDeviceId);

        ack.Seq.Should().Be(3);
        _messageRepo.Verify(r => r.AppendAsync(envelope), Times.Once);
    }
}
