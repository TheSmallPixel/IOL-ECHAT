using ECHAT.Client.Core.Commands;
using ECHAT.Client.Core.Interfaces;

namespace ECHAT.Client.Core.Services;

public class InMemoryOutboxStore : IOutboxStore
{
    private List<OutboxItem> _items = new();

    public IReadOnlyList<OutboxItem> Load() => _items;

    public void Save(IEnumerable<OutboxItem> items) => _items = items.ToList();
}
