using System.Text.Json;
using ECHAT.Client.Core.Interfaces;

namespace ECHAT.Client.Core.Tests.Fakes;

/// <summary>
/// Transport in-memory per i test, con round-trip JSON come il LocalStorageService reale
/// (così la deep-copy / serializzazione è rappresentativa).
/// </summary>
public class InMemoryLocalStorageTransport : ILocalStorageTransport
{
    private readonly Dictionary<string, string> _data = new();

    public Task<T?> GetAsync<T>(string key)
    {
        if (!_data.TryGetValue(key, out var raw) || string.IsNullOrEmpty(raw))
            return Task.FromResult<T?>(default);
        return Task.FromResult(JsonSerializer.Deserialize<T>(raw));
    }

    public Task SetAsync<T>(string key, T value)
    {
        _data[key] = JsonSerializer.Serialize(value);
        return Task.CompletedTask;
    }

    public Task RemoveAsync(string key)
    {
        _data.Remove(key);
        return Task.CompletedTask;
    }

    public bool ContainsKey(string key) => _data.ContainsKey(key);
}
