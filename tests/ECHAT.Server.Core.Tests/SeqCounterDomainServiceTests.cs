using ECHAT.Server.Core.Interfaces;
using ECHAT.Server.Core.Services;
using FluentAssertions;

namespace ECHAT.Server.Core.Tests;

public class SeqCounterDomainServiceTests
{
    private readonly SeqCounterDomainService _service = new();

    [Fact]
    public void ReserveRange_NextSeqBehindAnchor_SelfHealsToAnchorPlusOne()
    {
        var current = new SeqCounter { NextSeq = 3, AnchorSeq = 10, AnchorEnvelopeHash = new byte[] { 9 } };

        var result = _service.ReserveRange(current, 1, maxMessageSeq: 0);

        result.Reservation.StartSeq.Should().Be(11);
        result.Reservation.EndSeq.Should().Be(11);
        result.NewNextSeq.Should().Be(12);
    }

    [Fact]
    public void ReserveRange_AnchorZeroWithMaxMessageSeq_StartsAtMaxPlusOne()
    {
        var current = new SeqCounter { NextSeq = 1, AnchorSeq = 0 };

        var result = _service.ReserveRange(current, 1, maxMessageSeq: 42);

        result.Reservation.StartSeq.Should().Be(43);
        result.NewNextSeq.Should().Be(44);
    }

    [Fact]
    public void ReserveRange_AnchorPositive_StartsAtAnchorPlusOne()
    {
        var current = new SeqCounter { NextSeq = 1, AnchorSeq = 7 };

        var result = _service.ReserveRange(current, 1, maxMessageSeq: 100);

        // AnchorSeq > 0 ignora maxMessageSeq nel calcolo del minStart.
        result.Reservation.StartSeq.Should().Be(8);
        result.NewNextSeq.Should().Be(9);
    }

    [Fact]
    public void ReserveRange_CountFiveFromTen_ComputesEndAndNext()
    {
        var current = new SeqCounter { NextSeq = 10, AnchorSeq = 0 };

        var result = _service.ReserveRange(current, 5, maxMessageSeq: 0);

        result.Reservation.StartSeq.Should().Be(10);
        result.Reservation.EndSeq.Should().Be(14);
        result.NewNextSeq.Should().Be(15);
    }

    [Fact]
    public void ReserveRange_CarriesAnchorThrough()
    {
        var hash = new byte[] { 1, 2, 3 };
        var current = new SeqCounter { NextSeq = 5, AnchorSeq = 3, AnchorEnvelopeHash = hash };

        var result = _service.ReserveRange(current, 2, maxMessageSeq: 0);

        result.Reservation.AnchorSeq.Should().Be(3);
        result.Reservation.AnchorEnvelopeHash.Should().BeSameAs(hash);
    }

    [Theory]
    [InlineData(5, 5, false)]
    [InlineData(5, 4, false)]
    [InlineData(5, 6, true)]
    public void CanUpdateAnchor_OnlyAcceptsStrictlyGreater(long currentAnchor, long proposed, bool expected)
    {
        var current = new SeqCounter { AnchorSeq = currentAnchor };

        _service.CanUpdateAnchor(current, proposed).Should().Be(expected);
    }
}
