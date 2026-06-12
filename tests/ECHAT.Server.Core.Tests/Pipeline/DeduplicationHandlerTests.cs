using ECHAT.Models.Domain;
using ECHAT.Models.Enums;
using ECHAT.Server.Core.Interfaces;
using ECHAT.Server.Core.Pipeline;
using FluentAssertions;
using Moq;

namespace ECHAT.Server.Core.Tests.Pipeline;

public class DeduplicationHandlerTests
{
    private readonly Mock<IMessageRepository> _repo = new();

    private static MessageEnvelope Envelope(Guid? id = null) => new()
    {
        ConversationId = Guid.NewGuid(),
        MessageId = id ?? Guid.NewGuid(),
        Seq = 1,
        EpochId = 1,
        Type = MessageType.Text
    };

    [Fact]
    public async Task FirstSeen_CallsNext()
    {
        _repo.Setup(r => r.ExistsAsync(It.IsAny<Guid>())).ReturnsAsync(false);
        var handler = new DeduplicationHandler(_repo.Object);
        var ctx = new IngestContext(Envelope());
        var nextCalled = false;

        await handler.HandleAsync(ctx, () => { nextCalled = true; return Task.CompletedTask; });

        nextCalled.Should().BeTrue();
        ctx.IsDuplicate.Should().BeFalse();
    }

    [Fact]
    public async Task DuplicateMessageId_MarksContextAndShortCircuits()
    {
        _repo.Setup(r => r.ExistsAsync(It.IsAny<Guid>())).ReturnsAsync(true);
        var handler = new DeduplicationHandler(_repo.Object);
        var ctx = new IngestContext(Envelope());
        var nextCalled = false;

        await handler.HandleAsync(ctx, () => { nextCalled = true; return Task.CompletedTask; });

        ctx.IsDuplicate.Should().BeTrue();
        nextCalled.Should().BeFalse();
    }
}
