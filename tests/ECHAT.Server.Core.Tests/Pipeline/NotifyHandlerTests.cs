using ECHAT.Models.Domain;
using ECHAT.Models.Events;
using ECHAT.Server.Core.Interfaces;
using ECHAT.Server.Core.Pipeline;
using FluentAssertions;
using Moq;

namespace ECHAT.Server.Core.Tests.Pipeline;

public class NotifyHandlerTests
{
    [Fact]
    public async Task PublishesMessageAvailableEvent_WithEnvelopeFields()
    {
        var notifier = new Mock<IRealtimeNotifier>();
        var envelope = new MessageEnvelope
        {
            ConversationId = Guid.NewGuid(),
            MessageId = Guid.NewGuid(),
            Seq = 9,
            EpochId = 3
        };
        var nextCalled = false;

        var handler = new NotifyHandler(notifier.Object);
        await handler.HandleAsync(new IngestContext(envelope),
            () => { nextCalled = true; return Task.CompletedTask; });

        notifier.Verify(n => n.NotifyAsync(
            envelope.ConversationId,
            It.Is<MessageAvailableEvent>(e =>
                e.ConversationId == envelope.ConversationId
                && e.MessageId == envelope.MessageId
                && e.Seq == 9
                && e.EpochId == 3)), Times.Once);
        nextCalled.Should().BeTrue();
    }
}
