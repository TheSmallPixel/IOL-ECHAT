using ECHAT.Models.Domain;
using ECHAT.Models.Dtos;
using ECHAT.Server.Core.Interfaces;
using ECHAT.Server.Core.Pipeline;
using FluentAssertions;
using Moq;

namespace ECHAT.Server.Core.Tests.Pipeline;

/// <summary>
/// Covers the best-effort catch branch of <see cref="AuditHandler"/>: an audit-log failure on an
/// already-persisted message must NOT break the ingest chain; it is logged and next() still runs.
/// (Happy path + null-userId mapping live in AuditHandlerTests.)
/// </summary>
public class AuditHandlerCatchTests
{
    private static IngestContext Ctx() => new(new MessageEnvelope
    {
        ConversationId = Guid.NewGuid(),
        MessageId = Guid.NewGuid(),
        Seq = 3,
        EpochId = 1
    })
    { UserId = Guid.NewGuid() };

    [Fact]
    public async Task RecordThrows_DoesNotPropagate_AndStillCallsNext()
    {
        var audit = new Mock<IAuditLog>();
        audit
            .Setup(a => a.RecordAsync(It.IsAny<AuditEntry>()))
            .ThrowsAsync(new InvalidOperationException("audit store unavailable"));

        var nextCalled = false;
        var handler = new AuditHandler(audit.Object);

        Func<Task> act = () => handler.HandleAsync(Ctx(),
            () => { nextCalled = true; return Task.CompletedTask; });

        await act.Should().NotThrowAsync();
        nextCalled.Should().BeTrue();
        audit.Verify(a => a.RecordAsync(It.IsAny<AuditEntry>()), Times.Once);
    }

    [Fact]
    public async Task RecordThrowsSynchronously_StillSwallowedAndChainContinues()
    {
        var audit = new Mock<IAuditLog>();
        audit
            .Setup(a => a.RecordAsync(It.IsAny<AuditEntry>()))
            .Throws(new TimeoutException("db timeout"));

        var nextCalled = false;
        var handler = new AuditHandler(audit.Object);

        Func<Task> act = () => handler.HandleAsync(Ctx(),
            () => { nextCalled = true; return Task.CompletedTask; });

        await act.Should().NotThrowAsync();
        nextCalled.Should().BeTrue();
    }
}
