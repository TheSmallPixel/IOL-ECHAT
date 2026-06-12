using ECHAT.Server.App.Data;
using ECHAT.Server.App.Repositories;
using ECHAT.Server.Core.Services;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace ECHAT.Integration.Tests;

public class SequenceServiceTests : IDisposable
{
    private readonly EchatDbContext _db;
    private readonly SequenceService _sut;

    public SequenceServiceTests()
    {
        var options = new DbContextOptionsBuilder<EchatDbContext>()
            .UseInMemoryDatabase($"echat-{Guid.NewGuid()}")
            .Options;
        _db = new EchatDbContext(options);
        _sut = new SequenceService(
            new SeqCounterStore(_db, new ECHAT.Server.Core.Services.SeqCounterDomainService()),
            new SeqLeaseStore(_db),
            new MessageRepository(_db));
    }

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task ReserveRange_FirstCall_StartsAtSeqOne()
    {
        var conversationId = Guid.NewGuid();
        var deviceId = Guid.NewGuid();

        var reservation = await _sut.ReserveRangeAsync(conversationId, deviceId, count: 32);

        reservation.StartSeq.Should().Be(1);
        reservation.EndSeq.Should().Be(32);
        reservation.LeaseToken.Should().NotBeEmpty();
        reservation.AnchorSeq.Should().Be(0);
    }

    [Fact]
    public async Task ReserveRange_Subsequent_DoesNotOverlap()
    {
        var conversationId = Guid.NewGuid();
        var deviceId = Guid.NewGuid();

        var first = await _sut.ReserveRangeAsync(conversationId, deviceId, count: 10);
        var second = await _sut.ReserveRangeAsync(conversationId, deviceId, count: 10);

        second.StartSeq.Should().Be(first.EndSeq + 1);
        second.LeaseToken.Should().NotBe(first.LeaseToken);
    }

    [Fact]
    public async Task ReserveRange_NegativeOrZeroCount_Throws()
    {
        var act = async () => await _sut.ReserveRangeAsync(Guid.NewGuid(), Guid.NewGuid(), count: 0);
        await act.Should().ThrowAsync<ArgumentOutOfRangeException>();
    }

    [Fact]
    public async Task ValidateLease_KnownToken_AndSeqInRange_ReturnsTrue()
    {
        var conversationId = Guid.NewGuid();
        var reservation = await _sut.ReserveRangeAsync(conversationId, Guid.NewGuid(), count: 5);

        var ok = await _sut.ValidateLeaseAsync(conversationId, reservation.StartSeq + 2, reservation.LeaseToken);
        ok.Should().BeTrue();
    }

    [Fact]
    public async Task ValidateLease_UnknownToken_ReturnsFalse()
    {
        var ok = await _sut.ValidateLeaseAsync(Guid.NewGuid(), 1, "not-a-real-token");
        ok.Should().BeFalse();
    }

    [Fact]
    public async Task ValidateLease_EmptyToken_ReturnsFalse()
    {
        var ok = await _sut.ValidateLeaseAsync(Guid.NewGuid(), 1, "");
        ok.Should().BeFalse();
    }

    [Fact]
    public async Task ValidateLease_SeqOutsideRange_ReturnsFalse()
    {
        var conversationId = Guid.NewGuid();
        var reservation = await _sut.ReserveRangeAsync(conversationId, Guid.NewGuid(), count: 5);

        var below = await _sut.ValidateLeaseAsync(conversationId, reservation.StartSeq - 1, reservation.LeaseToken);
        var above = await _sut.ValidateLeaseAsync(conversationId, reservation.EndSeq + 1, reservation.LeaseToken);

        below.Should().BeFalse();
        above.Should().BeFalse();
    }

    [Fact]
    public async Task ValidateLease_WrongConversation_ReturnsFalse()
    {
        var conversationId = Guid.NewGuid();
        var reservation = await _sut.ReserveRangeAsync(conversationId, Guid.NewGuid(), count: 5);

        var ok = await _sut.ValidateLeaseAsync(Guid.NewGuid(), reservation.StartSeq, reservation.LeaseToken);
        ok.Should().BeFalse();
    }

    [Fact]
    public async Task ReserveRange_LegacyCounterBehindAnchor_JumpsForward()
    {
        // Simula una conversazione in cui `SeqCounters.NextSeq` è rimasto a 1 per via di codice legacy
        // mentre l'anchor è già avanzato. Senza self-heal, la prossima reservation darebbe un seq
        // già usato e l'INSERT fallirebbe per il vincolo UNIQUE.
        var conversationId = Guid.NewGuid();
        _db.SeqCounters.Add(new ECHAT.Server.App.Data.Entities.SeqCounterEntity
        {
            ConversationId = conversationId,
            NextSeq = 1,
            AnchorSeq = 50,
            AnchorEnvelopeHash = new byte[] { 1, 2, 3 }
        });
        await _db.SaveChangesAsync();

        var reservation = await _sut.ReserveRangeAsync(conversationId, Guid.NewGuid(), count: 10);

        reservation.StartSeq.Should().Be(51, "must skip past anchor");
        reservation.EndSeq.Should().Be(60);
    }

    [Fact]
    public async Task ReserveRange_LegacyCounterBehindMessages_JumpsForward()
    {
        // L'anchor non è mai stato aggiornato (dati legacy), ma esistono Messages con seq alti.
        var conversationId = Guid.NewGuid();
        _db.Messages.Add(new ECHAT.Server.App.Data.Entities.MessageEntity
        {
            ConversationId = conversationId,
            MessageId = Guid.NewGuid(),
            Seq = 99,
            EpochId = 1,
            Type = ECHAT.Models.Enums.MessageType.Text
        });
        _db.SeqCounters.Add(new ECHAT.Server.App.Data.Entities.SeqCounterEntity
        {
            ConversationId = conversationId,
            NextSeq = 1,
            AnchorSeq = 0,
            AnchorEnvelopeHash = Array.Empty<byte>()
        });
        await _db.SaveChangesAsync();

        var reservation = await _sut.ReserveRangeAsync(conversationId, Guid.NewGuid(), count: 5);

        reservation.StartSeq.Should().Be(100, "must skip past max persisted message seq");
    }

    [Fact]
    public async Task ValidateLease_ExpiredLease_ReturnsFalse()
    {
        var conversationId = Guid.NewGuid();
        var reservation = await _sut.ReserveRangeAsync(conversationId, Guid.NewGuid(), count: 5);

        // Forziamo la scadenza modificando direttamente la riga.
        var lease = await _db.SeqLeases.FirstAsync(l => l.LeaseToken == reservation.LeaseToken);
        lease.ExpiresAt = DateTime.UtcNow.AddSeconds(-1);
        await _db.SaveChangesAsync();

        var ok = await _sut.ValidateLeaseAsync(conversationId, reservation.StartSeq, reservation.LeaseToken);
        ok.Should().BeFalse();
    }
}
