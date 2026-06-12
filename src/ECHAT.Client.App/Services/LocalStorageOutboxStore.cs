using System.Text.Json;
using ECHAT.Client.Core.Commands;
using ECHAT.Client.Core.Interfaces;

namespace ECHAT.Client.App.Services;

/// <summary>
/// Storage dell'outbox su localStorage: gli invii in coda sopravvivono a refresh e riavvio del browser.
/// Carica una volta nel costruttore, poi rispecchia ogni Save su localStorage in modo sincrono.
/// </summary>
public class LocalStorageOutboxStore : IOutboxStore
{
    private const string StorageKey = "echat_outbox";

    private readonly LocalStorageService _storage;
    private List<OutboxItem> _items;

    public LocalStorageOutboxStore(LocalStorageService storage)
    {
        _storage = storage;
        var raw = _storage.GetRaw(StorageKey);
        _items = string.IsNullOrEmpty(raw)
            ? new List<OutboxItem>()
            : (JsonSerializer.Deserialize<List<OutboxItem>>(raw) ?? new List<OutboxItem>());
    }

    public IReadOnlyList<OutboxItem> Load() => _items;

    public void Save(IEnumerable<OutboxItem> items)
    {
        _items = items.ToList();
        _storage.SetRaw(StorageKey, JsonSerializer.Serialize(_items));
    }
}
