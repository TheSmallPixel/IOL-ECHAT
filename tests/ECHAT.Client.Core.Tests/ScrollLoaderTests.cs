using ECHAT.Client.Core.Services;
using ECHAT.Client.Core.Tests.Fakes;
using ECHAT.Models.Domain;
using FluentAssertions;

namespace ECHAT.Client.Core.Tests;

public class ScrollLoaderTests
{
    private readonly Guid _conv = Guid.NewGuid();
    private readonly FakeChatSdk _sdk = new();
    private readonly FakeLocalStore _store = new();

    private ScrollLoader Sut() => new(_sdk, _store);

    private DecryptedMessage Msg(long seq) => new()
    {
        MessageId = Guid.NewGuid(),
        Seq = seq,
        Payload = new MessagePayload { Seq = seq, Text = $"m{seq}" }
    };

    [Fact]
    public async Task LoadMore_ReturnsCachedWhenSufficient_NoFetch()
    {
        for (int i = 1; i <= 20; i++) await _store.StoreMessageAsync(_conv, Msg(i));

        var result = await Sut().LoadMoreAsync(_conv, beforeSeq: 100, limit: 10);

        result.Should().HaveCount(10);
        _sdk.FetchCalls.Should().BeEmpty("the cache held enough messages");
    }

    [Fact]
    public async Task LoadMore_FallsBackToFetch_WhenCacheInsufficient_AndStores()
    {
        await _store.StoreMessageAsync(_conv, Msg(1));
        for (int i = 1; i <= 30; i++) _sdk.ServerMessages.GetValueOrDefault(_conv, new List<DecryptedMessage>());
        _sdk.ServerMessages[_conv] = Enumerable.Range(1, 30).Select(i => Msg(i)).ToList();

        var result = await Sut().LoadMoreAsync(_conv, beforeSeq: 100, limit: 20);

        result.Should().HaveCount(20);
        _sdk.FetchCalls.Should().ContainSingle();
        _store.Stored[_conv].Should().HaveCountGreaterThan(1, "fetched messages get stored back");
    }

    [Fact]
    public async Task LoadRecent_AlwaysFetches_StoresResults_AndAdvancesLastApplied()
    {
        _sdk.ServerMessages[_conv] = new List<DecryptedMessage> { Msg(5), Msg(6), Msg(7) };

        var result = await Sut().LoadRecentAsync(_conv, afterSeq: 4, limit: 10);

        result.Should().HaveCount(3);
        _sdk.FetchCalls.Should().ContainSingle();
        _store.Stored[_conv].Should().HaveCount(3);
        (await _store.GetLastAppliedSeqAsync(_conv)).Should().Be(7);
    }

    [Fact]
    public async Task LoadRecent_NoNewMessages_DoesNotTouchLastApplied()
    {
        _sdk.ServerMessages[_conv] = new List<DecryptedMessage>();

        var result = await Sut().LoadRecentAsync(_conv, afterSeq: 10, limit: 10);

        result.Should().BeEmpty();
        (await _store.GetLastAppliedSeqAsync(_conv)).Should().BeNull();
    }
}
