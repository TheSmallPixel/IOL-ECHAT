using ECHAT.Models.Domain;
using ECHAT.Models.Enums;
using ECHAT.Server.App.Data;
using ECHAT.Server.App.Repositories;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace ECHAT.Integration.Tests;

/// <summary>
/// Test dello store EF dei messaggi: round-trip dell'envelope (incl. il nuovo <c>SenderUserId</c> di S4),
/// paginazione a cursore (after/before/default), latest, max-seq, conteggio per epoch, dedup per
/// messageId, lookup mittente-per-conversazione (usato per la verifica firme storiche) e replace.
/// </summary>
public class MessageRepositoryTests : IDisposable
{
    private readonly EchatDbContext _db;
    private readonly MessageRepository _sut;
    private readonly Guid _conv = Guid.NewGuid();

    public MessageRepositoryTests()
    {
        var options = new DbContextOptionsBuilder<EchatDbContext>()
            .UseInMemoryDatabase($"echat-{Guid.NewGuid()}")
            .Options;
        _db = new EchatDbContext(options);
        _sut = new MessageRepository(_db);
    }

    public void Dispose() => _db.Dispose();

    private static MessageEnvelope Env(Guid conv, long seq, Guid? sender = null, Guid? senderUser = null, int epoch = 1)
        => new()
        {
            ConversationId = conv,
            MessageId = Guid.NewGuid(),
            Seq = seq,
            EpochId = epoch,
            SenderDeviceId = sender ?? Guid.NewGuid(),
            SenderUserId = senderUser ?? Guid.NewGuid(),
            Nonce = new byte[] { (byte)seq },
            Ciphertext = new byte[] { 0xA1, (byte)seq },
            Signature = new byte[] { 0x09 },
            LeaseToken = "t",
            Type = MessageType.Text,
        };

    private async Task Seed(params MessageEnvelope[] envs)
    {
        foreach (var e in envs) await _sut.AppendAsync(e);
    }

    [Fact]
    public async Task Append_Query_RoundTrips_IncludingSenderUserId()
    {
        var sender = Guid.NewGuid();
        var senderUser = Guid.NewGuid();
        var e = Env(_conv, 1, sender, senderUser);
        await _sut.AppendAsync(e);

        var got = (await _sut.QueryAsync(_conv, null, null, 100)).Single();
        got.Seq.Should().Be(1);
        got.SenderDeviceId.Should().Be(sender);
        got.SenderUserId.Should().Be(senderUser);       // S4 field survives the round-trip
        got.Ciphertext.Should().Equal(0xA1, 1);
    }

    [Fact]
    public async Task Query_AfterSeq_ReturnsForwardTail()
    {
        await Seed(Env(_conv, 1), Env(_conv, 2), Env(_conv, 3), Env(_conv, 4));
        var page = await _sut.QueryAsync(_conv, afterSeq: 2, beforeSeq: null, limit: 10);
        page.Select(m => m.Seq).Should().Equal(3, 4);
    }

    [Fact]
    public async Task Query_BeforeSeq_ReturnsEarlierAscending_Limited()
    {
        await Seed(Env(_conv, 1), Env(_conv, 2), Env(_conv, 3), Env(_conv, 4));
        var page = await _sut.QueryAsync(_conv, afterSeq: null, beforeSeq: 4, limit: 2);
        page.Select(m => m.Seq).Should().Equal(2, 3); // last 2 below seq 4, ascending
    }

    [Fact]
    public async Task Query_Default_ReturnsLatestAscending()
    {
        await Seed(Env(_conv, 1), Env(_conv, 2), Env(_conv, 3));
        var page = await _sut.QueryAsync(_conv, null, null, 2);
        page.Select(m => m.Seq).Should().Equal(2, 3);
    }

    [Fact]
    public async Task QueryLatest_ReturnsDescending()
    {
        await Seed(Env(_conv, 1), Env(_conv, 2), Env(_conv, 3));
        var latest = await _sut.QueryLatestAsync(_conv, 2);
        latest.Select(m => m.Seq).Should().Equal(3, 2);
    }

    [Fact]
    public async Task GetMaxSeq_EmptyIsZero_ThenMax()
    {
        (await _sut.GetMaxSeqAsync(_conv)).Should().Be(0);
        await Seed(Env(_conv, 1), Env(_conv, 5), Env(_conv, 3));
        (await _sut.GetMaxSeqAsync(_conv)).Should().Be(5);
    }

    [Fact]
    public async Task CountByEpochBelow_CountsOnlyOlderEpochs()
    {
        await Seed(Env(_conv, 1, epoch: 1), Env(_conv, 2, epoch: 1), Env(_conv, 3, epoch: 2));
        (await _sut.CountByEpochBelowAsync(_conv, epochThreshold: 2)).Should().Be(2);
        (await _sut.CountByEpochBelowAsync(_conv, epochThreshold: 1)).Should().Be(0);
    }

    [Fact]
    public async Task Exists_ByMessageId()
    {
        var e = Env(_conv, 1);
        await _sut.AppendAsync(e);
        (await _sut.ExistsAsync(e.MessageId)).Should().BeTrue();
        (await _sut.ExistsAsync(Guid.NewGuid())).Should().BeFalse();
    }

    [Fact]
    public async Task HasMessageFromDevice_IsScopedToConversation()
    {
        var dev = Guid.NewGuid();
        var otherConv = Guid.NewGuid();
        await _sut.AppendAsync(Env(_conv, 1, sender: dev));
        await _sut.AppendAsync(Env(otherConv, 1, sender: Guid.NewGuid()));

        (await _sut.HasMessageFromDeviceAsync(_conv, dev)).Should().BeTrue();
        (await _sut.HasMessageFromDeviceAsync(_conv, Guid.NewGuid())).Should().BeFalse();
        (await _sut.HasMessageFromDeviceAsync(otherConv, dev)).Should().BeFalse(); // device didn't send THERE
    }

    [Fact]
    public async Task Replace_OverwritesEnvelopeAtSeq()
    {
        await _sut.AppendAsync(Env(_conv, 1));
        var custodian = Guid.NewGuid();
        var custodianUser = Guid.NewGuid();
        var replacement = Env(_conv, 1, sender: custodian, senderUser: custodianUser, epoch: 2);

        await _sut.ReplaceAsync(seq: 1, newEnvelope: replacement);

        var got = (await _sut.QueryAsync(_conv, null, null, 100)).Single();
        got.EpochId.Should().Be(2);
        got.SenderDeviceId.Should().Be(custodian);
        got.SenderUserId.Should().Be(custodianUser); // custodian re-attribution persists
    }

    [Fact]
    public async Task Replace_MissingSeq_Throws()
    {
        Func<Task> act = () => _sut.ReplaceAsync(seq: 99, newEnvelope: Env(_conv, 99));
        await act.Should().ThrowAsync<InvalidOperationException>();
    }
}
