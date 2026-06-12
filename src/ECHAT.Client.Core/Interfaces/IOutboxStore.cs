using ECHAT.Client.Core.Commands;

namespace ECHAT.Client.Core.Interfaces;

public interface IOutboxStore
{
    IReadOnlyList<OutboxItem> Load();
    void Save(IEnumerable<OutboxItem> items);
}
