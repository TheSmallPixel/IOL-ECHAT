using ECHAT.Models.Domain;
using ECHAT.Models.Enums;
using ECHAT.Server.Core.Interfaces;
using ECHAT.Server.Core.Services;
using FluentAssertions;
using Moq;

namespace ECHAT.Server.Core.Tests;

public class TombstoneInjectionServiceTests
{
    private readonly Mock<IMessageRepository> _messages = new();
    private readonly Mock<ISeqCounterStore> _counter = new();
    private readonly TombstoneInjectionService _sut;

    public TombstoneInjectionServiceTests()
    {
        _sut = new TombstoneInjectionService(_messages.Object, _counter.Object);
    }

    private void SetupAnchor(long anchorSeq, byte[]? hash = null)
    {
        _counter.Setup(c => c.GetAsync(It.IsAny<Guid>()))
            .ReturnsAsync(new SeqCounter { AnchorSeq = anchorSeq, AnchorEnvelopeHash = hash ?? Array.Empty<byte>() });
    }

    [Fact]
    public async Task InjectTombstonesAsync_EmptyList_ReturnsValidationError()
    {
        var result = await _sut.InjectTombstonesAsync(Guid.NewGuid(), Guid.NewGuid(), new List<TombstoneSpec>());

        result.Succeeded.Should().BeFalse();
        result.Error.Should().Be("No tombstones provided.");
        _messages.Verify(m => m.AppendAsync(It.IsAny<MessageEnvelope>()), Times.Never);
    }

    [Fact]
    public async Task InjectTombstonesAsync_NullList_ReturnsValidationError()
    {
        var result = await _sut.InjectTombstonesAsync(Guid.NewGuid(), Guid.NewGuid(), null!);

        result.Succeeded.Should().BeFalse();
        result.Error.Should().Be("No tombstones provided.");
    }

    [Fact]
    public async Task InjectTombstonesAsync_FirstSeqAtOrBelowAnchor_ReturnsError()
    {
        SetupAnchor(anchorSeq: 10);
        var specs = new List<TombstoneSpec> { new() { Seq = 10, EpochId = 1 } };

        var result = await _sut.InjectTombstonesAsync(Guid.NewGuid(), Guid.NewGuid(), specs);

        result.Succeeded.Should().BeFalse();
        result.Error.Should().Be("Tombstone seq 10 <= current anchor 10.");
        _messages.Verify(m => m.AppendAsync(It.IsAny<MessageEnvelope>()), Times.Never);
        _counter.Verify(c => c.UpdateAnchorAsync(It.IsAny<Guid>(), It.IsAny<long>(), It.IsAny<byte[]>()), Times.Never);
    }

    [Fact]
    public async Task InjectTombstonesAsync_ValidOrderedSpecs_PersistsWithChainedHashes()
    {
        SetupAnchor(anchorSeq: 5);
        var conversationId = Guid.NewGuid();
        var senderId = Guid.NewGuid();
        var appended = new List<MessageEnvelope>();
        _messages.Setup(m => m.AppendAsync(It.IsAny<MessageEnvelope>()))
            .Callback<MessageEnvelope>(e => appended.Add(e))
            .Returns(Task.CompletedTask);

        // Provided out of order to verify the service sorts by Seq.
        var specs = new List<TombstoneSpec>
        {
            new() { Seq = 8, EpochId = 2, Ciphertext = new byte[] { 1 } },
            new() { Seq = 6, EpochId = 2, Ciphertext = new byte[] { 2 } },
            new() { Seq = 7, EpochId = 2, Ciphertext = new byte[] { 3 } },
        };

        var result = await _sut.InjectTombstonesAsync(conversationId, senderId, specs);

        result.Succeeded.Should().BeTrue();
        appended.Should().HaveCount(3);
        appended.Select(e => e.Seq).Should().ContainInOrder(6L, 7L, 8L);
        appended.Should().OnlyContain(e => e.Type == MessageType.GapTombstone);
        appended.Should().OnlyContain(e => e.SenderDeviceId == senderId);
        appended.Should().OnlyContain(e => e.ConversationId == conversationId);
        appended.Should().OnlyContain(e => e.MessageId != Guid.Empty);
    }

    [Fact]
    public async Task InjectTombstonesAsync_UpdatesAnchorWithFinalSeqAndHash()
    {
        SetupAnchor(anchorSeq: 5);
        var conversationId = Guid.NewGuid();
        var senderId = Guid.NewGuid();
        MessageEnvelope? last = null;
        _messages.Setup(m => m.AppendAsync(It.IsAny<MessageEnvelope>()))
            .Callback<MessageEnvelope>(e => last = e)
            .Returns(Task.CompletedTask);

        long capturedSeq = 0;
        byte[]? capturedHash = null;
        _counter.Setup(c => c.UpdateAnchorAsync(conversationId, It.IsAny<long>(), It.IsAny<byte[]>()))
            .Callback<Guid, long, byte[]>((_, seq, hash) => { capturedSeq = seq; capturedHash = hash; })
            .Returns(Task.CompletedTask);

        var specs = new List<TombstoneSpec>
        {
            new() { Seq = 6, EpochId = 2, Ciphertext = new byte[] { 1 } },
            new() { Seq = 9, EpochId = 3, Ciphertext = new byte[] { 2 } },
        };

        var result = await _sut.InjectTombstonesAsync(conversationId, senderId, specs);

        capturedSeq.Should().Be(9);
        capturedHash.Should().Equal(EnvelopeHasher.Compute(last!));
        result.AnchorSeq.Should().Be(9);
        result.Count.Should().Be(2);
        result.FromSeq.Should().Be(6);
        result.ToSeq.Should().Be(9);
        result.LastEpochId.Should().Be(3);
    }
}
