using ECHAT.Client.Core.Services;
using ECHAT.Client.Core.Tests.Fakes;
using ECHAT.Models.Domain;
using FluentAssertions;

namespace ECHAT.Client.Core.Tests;

public class BrowserLocalStoreImplTests
{
    private readonly InMemoryLocalStorageTransport _transport = new();
    private readonly BrowserLocalStoreImpl _store;
    private readonly Guid _conv = Guid.NewGuid();

    public BrowserLocalStoreImplTests()
    {
        _store = new BrowserLocalStoreImpl(_transport);
    }

    private static DecryptedMessage Msg(long seq, Guid? id = null)
        => new()
        {
            MessageId = id ?? Guid.NewGuid(),
            Seq = seq,
            Payload = new MessagePayload { Seq = seq, Text = $"msg-{seq}" }
        };

    [Fact]
    public async Task StoreMessage_IgnoresDuplicateMessageId()
    {
        var id = Guid.NewGuid();
        await _store.StoreMessageAsync(_conv, Msg(1, id));
        await _store.StoreMessageAsync(_conv, Msg(1, id));

        var all = await _store.GetMessagesAsync(_conv, null, null, 100);
        all.Should().HaveCount(1);
    }

    [Fact]
    public async Task StoreMessage_KeepsListOrderedBySeqAscending()
    {
        await _store.StoreMessageAsync(_conv, Msg(3));
        await _store.StoreMessageAsync(_conv, Msg(1));
        await _store.StoreMessageAsync(_conv, Msg(2));

        var all = await _store.GetMessagesAsync(_conv, null, null, 100);
        all.Select(m => m.Seq).Should().ContainInOrder(1L, 2L, 3L);
    }

    [Fact]
    public async Task GetMessages_AfterSeq_IsExclusive()
    {
        for (long s = 1; s <= 5; s++) await _store.StoreMessageAsync(_conv, Msg(s));

        var result = await _store.GetMessagesAsync(_conv, afterSeq: 2, beforeSeq: null, limit: 100);
        result.Select(m => m.Seq).Should().Equal(3L, 4L, 5L);
    }

    [Fact]
    public async Task GetMessages_BeforeSeq_IsExclusive()
    {
        for (long s = 1; s <= 5; s++) await _store.StoreMessageAsync(_conv, Msg(s));

        var result = await _store.GetMessagesAsync(_conv, afterSeq: null, beforeSeq: 4, limit: 100);
        result.Select(m => m.Seq).Should().Equal(1L, 2L, 3L);
    }

    [Fact]
    public async Task GetMessages_AfterAndBefore_BoundsBothApply()
    {
        for (long s = 1; s <= 5; s++) await _store.StoreMessageAsync(_conv, Msg(s));

        var result = await _store.GetMessagesAsync(_conv, afterSeq: 1, beforeSeq: 5, limit: 100);
        result.Select(m => m.Seq).Should().Equal(2L, 3L, 4L);
    }

    [Fact]
    public async Task GetMessages_RespectsLimit()
    {
        for (long s = 1; s <= 10; s++) await _store.StoreMessageAsync(_conv, Msg(s));

        var result = await _store.GetMessagesAsync(_conv, null, null, limit: 3);
        result.Should().HaveCount(3);
        result.Select(m => m.Seq).Should().Equal(1L, 2L, 3L);
    }

    [Fact]
    public async Task GetMessages_EmptyStore_ReturnsEmpty()
    {
        var result = await _store.GetMessagesAsync(_conv, null, null, 100);
        result.Should().BeEmpty();
    }

    [Fact]
    public async Task GetLastAppliedSeq_NullWhenUnset()
    {
        (await _store.GetLastAppliedSeqAsync(_conv)).Should().BeNull();
    }

    [Fact]
    public async Task SetThenGetLastAppliedSeq_RoundTrips_IncludingZero()
    {
        await _store.SetLastAppliedSeqAsync(_conv, 0);
        (await _store.GetLastAppliedSeqAsync(_conv)).Should().Be(0);

        await _store.SetLastAppliedSeqAsync(_conv, 42);
        (await _store.GetLastAppliedSeqAsync(_conv)).Should().Be(42);
    }

    [Fact]
    public async Task ClearConversation_RemovesMessagesAndSeq()
    {
        await _store.StoreMessageAsync(_conv, Msg(1));
        await _store.SetLastAppliedSeqAsync(_conv, 1);

        await _store.ClearConversationAsync(_conv);

        (await _store.GetMessagesAsync(_conv, null, null, 100)).Should().BeEmpty();
        (await _store.GetLastAppliedSeqAsync(_conv)).Should().BeNull();
    }
}
