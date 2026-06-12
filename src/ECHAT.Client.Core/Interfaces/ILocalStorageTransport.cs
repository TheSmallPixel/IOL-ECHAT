namespace ECHAT.Client.Core.Interfaces;

/// <summary>
/// Astrazione sul key/value store del browser (localStorage), modellata sulla superficie di
/// <c>LocalStorageService</c> dell'App. Permette di tenere la logica del cache messaggi in
/// Client.Core senza dipendere da JSInterop: l'App fornisce l'adapter, i test un fake in-memory.
/// </summary>
public interface ILocalStorageTransport
{
    Task<T?> GetAsync<T>(string key);
    Task SetAsync<T>(string key, T value);
    Task RemoveAsync(string key);
}
