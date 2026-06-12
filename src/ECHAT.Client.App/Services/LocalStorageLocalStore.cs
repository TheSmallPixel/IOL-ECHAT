using ECHAT.Client.Core.Interfaces;

namespace ECHAT.Client.App.Services;

/// <summary>
/// Adapter dell'App che implementa <see cref="ILocalStorageTransport"/> avvolgendo
/// <see cref="LocalStorageService"/> (JSInterop). La logica del cache messaggi vive in
/// <see cref="ECHAT.Client.Core.Services.BrowserLocalStoreImpl"/>; questo tipo fa solo da ponte.
/// </summary>
public class LocalStorageTransport : ILocalStorageTransport
{
    private readonly LocalStorageService _storage;

    public LocalStorageTransport(LocalStorageService storage)
    {
        _storage = storage;
    }

    public Task<T?> GetAsync<T>(string key) => _storage.GetAsync<T>(key);

    public Task SetAsync<T>(string key, T value) => _storage.SetAsync(key, value);

    public Task RemoveAsync(string key) => _storage.RemoveAsync(key);
}
