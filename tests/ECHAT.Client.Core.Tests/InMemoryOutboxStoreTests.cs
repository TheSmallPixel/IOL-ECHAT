using ECHAT.Client.Core.Commands;
using ECHAT.Client.Core.Services;
using ECHAT.Models.Enums;
using FluentAssertions;

namespace ECHAT.Client.Core.Tests;

public class InMemoryOutboxStoreTests
{
    [Fact]
    public void EmptyByDefault()
    {
        var store = new InMemoryOutboxStore();
        store.Load().Should().BeEmpty();
    }

    [Fact]
    public void Save_OverwritesPreviousList()
    {
        var store = new InMemoryOutboxStore();
        store.Save(new[] { new OutboxItem { MessageId = Guid.NewGuid(), State = OutboxState.Pending } });
        store.Load().Should().HaveCount(1);

        store.Save(Array.Empty<OutboxItem>());
        store.Load().Should().BeEmpty();
    }

    [Fact]
    public void Load_ReturnsItemsInOrder()
    {
        var store = new InMemoryOutboxStore();
        var a = new OutboxItem { MessageId = Guid.NewGuid() };
        var b = new OutboxItem { MessageId = Guid.NewGuid() };
        store.Save(new[] { a, b });

        store.Load().Should().ContainInOrder(a, b);
    }
}
