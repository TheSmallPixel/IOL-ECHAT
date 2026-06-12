using ECHAT.Models.Domain;
using ECHAT.Server.Core.Pipeline;
using FluentAssertions;

namespace ECHAT.Server.Core.Tests.Pipeline;

public class IngestContextTests
{
    [Fact]
    public void StoresEnvelope_AndExposesMutableState()
    {
        var envelope = new MessageEnvelope { Seq = 1 };
        var ctx = new IngestContext(envelope);

        ctx.Envelope.Should().BeSameAs(envelope);
        ctx.IsDuplicate.Should().BeFalse();
        ctx.Ack.Should().BeNull();
        ctx.UserId.Should().Be(Guid.Empty);

        ctx.IsDuplicate = true;
        ctx.UserId = Guid.NewGuid();
        ctx.Ack = new Models.Dtos.MessageAck { Seq = 1, AcceptedAt = DateTime.UtcNow };

        ctx.IsDuplicate.Should().BeTrue();
        ctx.UserId.Should().NotBe(Guid.Empty);
        ctx.Ack.Should().NotBeNull();
    }
}
