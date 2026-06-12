using ECHAT.Models.Domain;
using ECHAT.Models.Events;
using ECHAT.Server.Core.Interfaces;
using ECHAT.Server.Core.Pipeline;
using FluentAssertions;
using Moq;

namespace ECHAT.Server.Core.Tests.Pipeline;

/// <summary>
/// Covers the fire-and-forget catch branch of <see cref="NotifyHandler"/>: a realtime notify
/// failure must NOT turn an already-persisted ingest into a 500; it is logged and the chain
/// continues. (The happy path lives in NotifyHandlerTests.)
/// </summary>
public class NotifyHandlerCatchTests
{
    private static IngestContext Ctx() => new(new MessageEnvelope
    {
        ConversationId = Guid.NewGuid(),
        MessageId = Guid.NewGuid(),
        Seq = 7,
        EpochId = 1
    });

    [Fact]
    public async Task NotifyThrows_DoesNotPropagate_AndStillCallsNext()
    {
        var notifier = new Mock<IRealtimeNotifier>();
        notifier
            .Setup(n => n.NotifyAsync(It.IsAny<Guid>(), It.IsAny<RealtimeEvent>()))
            .ThrowsAsync(new InvalidOperationException("SignalR down"));

        var nextCalled = false;
        var handler = new NotifyHandler(notifier.Object);

        Func<Task> act = () => handler.HandleAsync(Ctx(),
            () => { nextCalled = true; return Task.CompletedTask; });

        // The message is already persisted upstream: a notify failure must be swallowed.
        await act.Should().NotThrowAsync();
        nextCalled.Should().BeTrue();
        notifier.Verify(n => n.NotifyAsync(It.IsAny<Guid>(), It.IsAny<RealtimeEvent>()), Times.Once);
    }

    [Fact]
    public async Task NotifyThrowsSynchronously_StillSwallowedAndChainContinues()
    {
        // Some notifier implementations could throw before returning a Task at all.
        var notifier = new Mock<IRealtimeNotifier>();
        notifier
            .Setup(n => n.NotifyAsync(It.IsAny<Guid>(), It.IsAny<RealtimeEvent>()))
            .Throws(new TimeoutException("hub timeout"));

        var nextCalled = false;
        var handler = new NotifyHandler(notifier.Object);

        Func<Task> act = () => handler.HandleAsync(Ctx(),
            () => { nextCalled = true; return Task.CompletedTask; });

        await act.Should().NotThrowAsync();
        nextCalled.Should().BeTrue();
    }
}
