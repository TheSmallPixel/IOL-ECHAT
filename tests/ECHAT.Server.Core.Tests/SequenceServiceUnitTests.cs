using ECHAT.Server.Core.Interfaces;
using ECHAT.Server.Core.Services;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace ECHAT.Server.Core.Tests;

/// <summary>
/// Unit test (mockati) di <see cref="SequenceService"/>, la logica di anti-replay / validazione del
/// lease di sequenza. Mutation testing aveva mostrato che questo servizio non era coperto da test di
/// unità (solo dagli integration): ogni guardia di <c>ValidateLeaseAsync</c> poteva essere invertita/
/// rimossa senza far fallire alcun test di Core. Qui le copriamo tutte, ai bordi.
/// </summary>
public class SequenceServiceUnitTests
{
    private readonly Mock<ISeqCounterStore> _counters = new();
    private readonly Mock<ISeqLeaseStore> _leases = new();
    private readonly Mock<IMessageRepository> _messages = new();
    private readonly Guid _conv = Guid.NewGuid();

    private SequenceService Sut() => new(_counters.Object, _leases.Object, _messages.Object, NullLogger<SequenceService>.Instance);

    private SeqLeaseRecord Lease(long start, long end, DateTime? expires = null, Guid? conv = null) => new()
    {
        LeaseToken = "tok",
        ConversationId = conv ?? _conv,
        DeviceId = Guid.NewGuid(),
        StartSeq = start,
        EndSeq = end,
        IssuedAt = DateTime.UtcNow.AddMinutes(-1),
        ExpiresAt = expires ?? DateTime.UtcNow.AddMinutes(5)
    };

    // ---- ReserveRangeAsync ----

    [Fact]
    public async Task Reserve_CountZeroOrNegative_Throws()
    {
        Func<Task> z = () => Sut().ReserveRangeAsync(_conv, Guid.NewGuid(), 0);
        Func<Task> n = () => Sut().ReserveRangeAsync(_conv, Guid.NewGuid(), -1);
        await z.Should().ThrowAsync<ArgumentOutOfRangeException>();
        await n.Should().ThrowAsync<ArgumentOutOfRangeException>();
    }

    [Fact]
    public async Task Reserve_ReservesRange_StoresLease_ReturnsTokenAndAnchor()
    {
        var device = Guid.NewGuid();
        _messages.Setup(m => m.GetMaxSeqAsync(_conv)).ReturnsAsync(7);
        _counters.Setup(c => c.ReserveRangeAsync(_conv, 3, 7))
            .ReturnsAsync(new SeqRangeReservation { StartSeq = 8, EndSeq = 10, AnchorSeq = 5, AnchorEnvelopeHash = new byte[] { 0xAB } });
        SeqLeaseRecord? stored = null;
        _leases.Setup(l => l.AddAsync(It.IsAny<SeqLeaseRecord>()))
            .Callback<SeqLeaseRecord>(r => stored = r).Returns(Task.CompletedTask);

        var res = await Sut().ReserveRangeAsync(_conv, device, 3);

        res.StartSeq.Should().Be(8);
        res.EndSeq.Should().Be(10);
        res.AnchorSeq.Should().Be(5);
        res.AnchorEnvelopeHash.Should().Equal(0xAB);
        res.LeaseToken.Should().NotBeNullOrEmpty();
        stored.Should().NotBeNull();
        stored!.StartSeq.Should().Be(8);
        stored.EndSeq.Should().Be(10);
        stored.DeviceId.Should().Be(device);
        stored.LeaseToken.Should().Be(res.LeaseToken);
        stored.ExpiresAt.Should().BeAfter(stored.IssuedAt); // TTL applied
        // The maxMessageSeq from the message store is forwarded to the counter store.
        _counters.Verify(c => c.ReserveRangeAsync(_conv, 3, 7), Times.Once);
    }

    // ---- ValidateLeaseAsync ----

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    public async Task Validate_EmptyToken_False_NoLookup(string? token)
    {
        (await Sut().ValidateLeaseAsync(_conv, 1, token!)).Should().BeFalse();
        _leases.Verify(l => l.FindByTokenAsync(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task Validate_UnknownToken_False()
    {
        _leases.Setup(l => l.FindByTokenAsync("tok")).ReturnsAsync((SeqLeaseRecord?)null);
        (await Sut().ValidateLeaseAsync(_conv, 1, "tok")).Should().BeFalse();
    }

    [Fact]
    public async Task Validate_ConversationMismatch_False()
    {
        _leases.Setup(l => l.FindByTokenAsync("tok")).ReturnsAsync(Lease(1, 10, conv: Guid.NewGuid()));
        (await Sut().ValidateLeaseAsync(_conv, 5, "tok")).Should().BeFalse();
    }

    [Fact]
    public async Task Validate_ExpiredLease_False_AtBoundary()
    {
        // ExpiresAt slightly in the past  expired (the guard is `<= UtcNow`).
        _leases.Setup(l => l.FindByTokenAsync("tok")).ReturnsAsync(Lease(1, 10, expires: DateTime.UtcNow.AddMilliseconds(-1)));
        (await Sut().ValidateLeaseAsync(_conv, 5, "tok")).Should().BeFalse();
    }

    [Theory]
    [InlineData(4, false)]   // StartSeq-1  out of range
    [InlineData(5, true)]    // StartSeq  in range (boundary)
    [InlineData(10, true)]   // EndSeq  in range (boundary)
    [InlineData(11, false)]  // EndSeq+1  out of range
    public async Task Validate_SeqRange_BoundariesEnforced(long seq, bool expected)
    {
        _leases.Setup(l => l.FindByTokenAsync("tok")).ReturnsAsync(Lease(5, 10));
        (await Sut().ValidateLeaseAsync(_conv, seq, "tok")).Should().Be(expected);
    }
}
