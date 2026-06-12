using ECHAT.Client.Core.Commands;
using ECHAT.Client.Core.Interfaces;
using ECHAT.Client.Core.Services;
using ECHAT.Models.Domain;
using FluentAssertions;

namespace ECHAT.Client.Core.Tests;

public class OutboxServicePersistenceTests
{
    /// <summary>Spy di IOutboxStore che conta le chiamate a Save.</summary>
    private class SpyStore : IOutboxStore
    {
        public List<OutboxItem> Items = new();
        public int SaveCount;

        public IReadOnlyList<OutboxItem> Load() => Items;
        public void Save(IEnumerable<OutboxItem> items)
        {
            Items = items.ToList();
            SaveCount++;
        }
    }

    private static SendMessageCommand Command() => new()
    {
        MessageId = Guid.NewGuid(),
        ConversationId = Guid.NewGuid(),
        Payload = new MessagePayload { Seq = 1 }
    };

    [Fact]
    public async Task Enqueue_PersistsThroughStore()
    {
        var store = new SpyStore();
        var outbox = new OutboxService(store);

        await outbox.EnqueueAsync(Command());

        store.SaveCount.Should().Be(1);
        store.Items.Should().HaveCount(1);
    }

    [Fact]
    public async Task Ack_PersistsThroughStore()
    {
        var store = new SpyStore();
        var outbox = new OutboxService(store);
        var cmd = Command();
        await outbox.EnqueueAsync(cmd);
        store.SaveCount = 0;

        await outbox.AckAsync(cmd.MessageId);

        store.SaveCount.Should().Be(1);
    }

    [Fact]
    public async Task Fail_PersistsThroughStore_AndIncrementsRetry()
    {
        var store = new SpyStore();
        var outbox = new OutboxService(store);
        var cmd = Command();
        await outbox.EnqueueAsync(cmd);
        store.SaveCount = 0;

        await outbox.FailAsync(cmd.MessageId, "network");
        await outbox.FailAsync(cmd.MessageId, "network");

        store.SaveCount.Should().Be(2);
        store.Items.Single(i => i.MessageId == cmd.MessageId).RetryCount.Should().Be(2);
    }

    [Fact]
    public async Task ReusesExistingItems_OnReload()
    {
        var store = new SpyStore();
        var first = new OutboxService(store);
        var cmd = Command();
        await first.EnqueueAsync(cmd);

        // Simula l'avvio di un nuovo processo che rilegge lo store
        var reloaded = new OutboxService(store);
        var pending = await reloaded.GetPendingAsync();

        pending.Should().ContainSingle().Which.MessageId.Should().Be(cmd.MessageId);
    }
}
