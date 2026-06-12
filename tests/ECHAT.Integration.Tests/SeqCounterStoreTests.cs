using ECHAT.Server.App.Data;
using ECHAT.Server.App.Repositories;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace ECHAT.Integration.Tests;

public class SeqCounterStoreTests : IDisposable
{
    private readonly EchatDbContext _db;
    private readonly SeqCounterStore _sut;

    public SeqCounterStoreTests()
    {
        var options = new DbContextOptionsBuilder<EchatDbContext>()
            .UseInMemoryDatabase($"echat-{Guid.NewGuid()}")
            .Options;
        _db = new EchatDbContext(options);
        _sut = new SeqCounterStore(_db, new ECHAT.Server.Core.Services.SeqCounterDomainService());
    }

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task Get_UnknownConversation_ReturnsFreshCounter()
    {
        var counter = await _sut.GetAsync(Guid.NewGuid());
        counter.NextSeq.Should().Be(1);
        counter.AnchorSeq.Should().Be(0);
        counter.AnchorEnvelopeHash.Should().BeEmpty();
    }

    [Fact]
    public async Task UpdateAnchor_FromScratch_InsertsCounter()
    {
        var conversationId = Guid.NewGuid();
        var hash = new byte[] { 1, 2, 3 };

        await _sut.UpdateAnchorAsync(conversationId, anchorSeq: 5, hash);

        var counter = await _sut.GetAsync(conversationId);
        counter.AnchorSeq.Should().Be(5);
        counter.AnchorEnvelopeHash.Should().BeEquivalentTo(hash);
    }

    [Fact]
    public async Task UpdateAnchor_AdvancesForward_Only()
    {
        var conversationId = Guid.NewGuid();
        var first = new byte[] { 0xAA };
        var second = new byte[] { 0xBB };
        var stale = new byte[] { 0xCC };

        await _sut.UpdateAnchorAsync(conversationId, anchorSeq: 5, first);
        await _sut.UpdateAnchorAsync(conversationId, anchorSeq: 7, second);
        await _sut.UpdateAnchorAsync(conversationId, anchorSeq: 6, stale);

        var counter = await _sut.GetAsync(conversationId);
        counter.AnchorSeq.Should().Be(7, "stale lower seq must not regress the anchor");
        counter.AnchorEnvelopeHash.Should().BeEquivalentTo(second);
    }
}
