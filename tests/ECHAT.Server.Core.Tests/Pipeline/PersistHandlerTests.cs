using ECHAT.Models.Domain;
using ECHAT.Server.Core.Interfaces;
using ECHAT.Server.Core.Pipeline;
using FluentAssertions;
using Moq;

namespace ECHAT.Server.Core.Tests.Pipeline;

public class PersistHandlerTests
{
    [Fact]
    public async Task Append_AdvancesAnchor_ThenCallsNext()
    {
        var repo = new Mock<IMessageRepository>();
        var counter = new Mock<ISeqCounterStore>();
        var envelope = new MessageEnvelope
        {
            ConversationId = Guid.NewGuid(),
            MessageId = Guid.NewGuid(),
            Seq = 42,
            EpochId = 1,
            SenderDeviceId = Guid.NewGuid(),
            Ciphertext = new byte[] { 1, 2, 3 }
        };

        var nextCalled = false;
        var handler = new PersistHandler(repo.Object, counter.Object);

        await handler.HandleAsync(
            new IngestContext(envelope),
            () => { nextCalled = true; return Task.CompletedTask; });

        repo.Verify(r => r.AppendAsync(envelope), Times.Once);
        counter.Verify(c => c.UpdateAnchorAsync(
            envelope.ConversationId,
            42,
            It.Is<byte[]>(h => h.Length == 32)), Times.Once);
        nextCalled.Should().BeTrue();
    }
}
