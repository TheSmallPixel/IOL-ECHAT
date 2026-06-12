using ECHAT.Client.Core.Services;
using ECHAT.Models.Dtos;
using FluentAssertions;

namespace ECHAT.Client.Core.Tests;

public class SequenceLeaseManagerTests
{
    private static SeqReservation CreateReservation(long start = 1, long end = 5) => new()
    {
        StartSeq = start,
        EndSeq = end,
        LeaseToken = "token",
        AnchorSeq = start - 1,
        AnchorEnvelopeHash = new byte[] { 1, 2, 3 }
    };

    [Fact]
    public async Task GetNextSeqAsync_ShouldReturnSequentialValues()
    {
        var manager = new SequenceLeaseManager();
        var convId = Guid.NewGuid();
        manager.ApplyReservation(convId, CreateReservation(10, 12));

        var seq1 = await manager.GetNextSeqAsync(convId);
        var seq2 = await manager.GetNextSeqAsync(convId);
        var seq3 = await manager.GetNextSeqAsync(convId);

        seq1.Should().Be(10);
        seq2.Should().Be(11);
        seq3.Should().Be(12);
    }

    [Fact]
    public async Task GetNextSeqAsync_Exhausted_ShouldThrow()
    {
        var manager = new SequenceLeaseManager();
        var convId = Guid.NewGuid();
        manager.ApplyReservation(convId, CreateReservation(1, 1));

        await manager.GetNextSeqAsync(convId);

        var act = async () => await manager.GetNextSeqAsync(convId);
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*exhausted*");
    }

    [Fact]
    public void HasAvailableSeq_WithLease_ShouldReturnTrue()
    {
        var manager = new SequenceLeaseManager();
        var convId = Guid.NewGuid();
        manager.ApplyReservation(convId, CreateReservation(1, 5));

        manager.HasAvailableSeq(convId).Should().BeTrue();
    }

    [Fact]
    public void HasAvailableSeq_NoLease_ShouldReturnFalse()
    {
        var manager = new SequenceLeaseManager();
        manager.HasAvailableSeq(Guid.NewGuid()).Should().BeFalse();
    }

    [Fact]
    public async Task GetAnchorHashAsync_ShouldReturnReservationHash()
    {
        var manager = new SequenceLeaseManager();
        var convId = Guid.NewGuid();
        manager.ApplyReservation(convId, CreateReservation());

        var hash = await manager.GetAnchorHashAsync(convId);
        hash.Should().BeEquivalentTo(new byte[] { 1, 2, 3 });
    }

    [Fact]
    public async Task GetNextSeqAsync_NoLease_ShouldThrow()
    {
        var manager = new SequenceLeaseManager();

        var act = async () => await manager.GetNextSeqAsync(Guid.NewGuid());
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*No lease*");
    }
}
