using ECHAT.Client.Core.Services;
using ECHAT.Models.Dtos;
using FluentAssertions;

namespace ECHAT.Client.Core.Tests;

public class SequenceLeaseManagerExtraTests
{
    [Fact]
    public async Task GetAnchorHashAsync_NoLease_Throws()
    {
        var manager = new SequenceLeaseManager();
        var act = async () => await manager.GetAnchorHashAsync(Guid.NewGuid());
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*No lease*");
    }

    [Fact]
    public async Task LeasesAreIsolated_PerConversation()
    {
        var manager = new SequenceLeaseManager();
        var convA = Guid.NewGuid();
        var convB = Guid.NewGuid();

        manager.ApplyReservation(convA, new SeqReservation { StartSeq = 1, EndSeq = 2 });
        manager.ApplyReservation(convB, new SeqReservation { StartSeq = 100, EndSeq = 101 });

        (await manager.GetNextSeqAsync(convA)).Should().Be(1);
        (await manager.GetNextSeqAsync(convB)).Should().Be(100);
        (await manager.GetNextSeqAsync(convA)).Should().Be(2);
        (await manager.GetNextSeqAsync(convB)).Should().Be(101);
    }

    [Fact]
    public void ApplyReservation_TwiceSameConversation_OverwritesPreviousLease()
    {
        var manager = new SequenceLeaseManager();
        var convId = Guid.NewGuid();
        manager.ApplyReservation(convId, new SeqReservation { StartSeq = 1, EndSeq = 2 });
        manager.ApplyReservation(convId, new SeqReservation { StartSeq = 50, EndSeq = 60 });

        manager.HasAvailableSeq(convId).Should().BeTrue();
    }

    [Fact]
    public void GetLeaseToken_NoReservation_ReturnsEmpty()
    {
        var manager = new SequenceLeaseManager();
        manager.GetLeaseToken(Guid.NewGuid()).Should().BeEmpty();
    }

    [Fact]
    public void GetLeaseToken_AfterApply_ReturnsTheToken()
    {
        var manager = new SequenceLeaseManager();
        var convId = Guid.NewGuid();
        manager.ApplyReservation(convId, new SeqReservation
        {
            StartSeq = 1,
            EndSeq = 5,
            LeaseToken = "abc123"
        });

        manager.GetLeaseToken(convId).Should().Be("abc123");
    }

    [Fact]
    public void GetLeaseToken_AfterReapply_ReturnsLatestToken()
    {
        var manager = new SequenceLeaseManager();
        var convId = Guid.NewGuid();
        manager.ApplyReservation(convId, new SeqReservation { StartSeq = 1, EndSeq = 2, LeaseToken = "first" });
        manager.ApplyReservation(convId, new SeqReservation { StartSeq = 50, EndSeq = 60, LeaseToken = "second" });

        manager.GetLeaseToken(convId).Should().Be("second");
    }
}
