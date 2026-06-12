using ECHAT.Models.Domain;
using ECHAT.Models.Dtos;
using ECHAT.Server.Core.Interfaces;
using ECHAT.Server.Core.Pipeline;
using FluentAssertions;
using Moq;

namespace ECHAT.Server.Core.Tests.Pipeline;

public class AuditHandlerTests
{
    [Fact]
    public async Task RecordsMessageIngestedEntry_WithUserId()
    {
        var audit = new Mock<IAuditLog>();
        var envelope = new MessageEnvelope
        {
            ConversationId = Guid.NewGuid(),
            MessageId = Guid.NewGuid(),
            Seq = 4,
            EpochId = 2
        };
        var ctx = new IngestContext(envelope) { UserId = Guid.NewGuid() };

        var handler = new AuditHandler(audit.Object);
        var nextCalled = false;
        await handler.HandleAsync(ctx, () => { nextCalled = true; return Task.CompletedTask; });

        audit.Verify(a => a.RecordAsync(It.Is<AuditEntry>(e =>
            e.ConversationId == envelope.ConversationId
            && e.UserId == ctx.UserId
            && e.Action == "MessageIngested"
            && e.Details!.Contains("seq=4")
            && e.Details!.Contains("epoch=2"))), Times.Once);
        nextCalled.Should().BeTrue();
    }

    [Fact]
    public async Task RecordsNullUserId_WhenContextHasEmptyGuid()
    {
        var audit = new Mock<IAuditLog>();
        var envelope = new MessageEnvelope { ConversationId = Guid.NewGuid(), MessageId = Guid.NewGuid(), Seq = 1 };
        var ctx = new IngestContext(envelope) { UserId = Guid.Empty };

        var handler = new AuditHandler(audit.Object);
        await handler.HandleAsync(ctx, () => Task.CompletedTask);

        audit.Verify(a => a.RecordAsync(It.Is<AuditEntry>(e => e.UserId == null)), Times.Once);
    }
}
