using ECHAT.Models.Domain;
using ECHAT.Server.Core.Interfaces;
using ECHAT.Server.Core.Pipeline;
using FluentAssertions;
using Moq;

namespace ECHAT.Server.Core.Tests.Pipeline;

public class LeaseValidationHandlerTests
{
    private readonly Mock<ISequenceService> _seq = new();

    private static MessageEnvelope Envelope() => new()
    {
        ConversationId = Guid.NewGuid(),
        MessageId = Guid.NewGuid(),
        Seq = 7,
        LeaseToken = "abc"
    };

    [Fact]
    public async Task ValidLease_CallsNext()
    {
        _seq.Setup(s => s.ValidateLeaseAsync(It.IsAny<Guid>(), 7, "abc")).ReturnsAsync(true);
        var handler = new LeaseValidationHandler(_seq.Object);
        var nextCalled = false;

        await handler.HandleAsync(new IngestContext(Envelope()),
            () => { nextCalled = true; return Task.CompletedTask; });

        nextCalled.Should().BeTrue();
    }

    [Fact]
    public async Task InvalidLease_Throws()
    {
        _seq.Setup(s => s.ValidateLeaseAsync(It.IsAny<Guid>(), It.IsAny<long>(), It.IsAny<string>()))
            .ReturnsAsync(false);
        var handler = new LeaseValidationHandler(_seq.Object);

        var act = async () => await handler.HandleAsync(
            new IngestContext(Envelope()),
            () => Task.CompletedTask);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*Invalid lease for seq 7*");
    }
}
