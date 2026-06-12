using System.Text.Json;
using Microsoft.JSInterop;

namespace ECHAT.Client.App.Services;

/// <summary>
/// Wrapper minimale sull'API localStorage del browser.
/// Sopravvive ai refresh; si perde solo se l'utente cancella i dati del sito.
/// </summary>
public class LocalStorageService
{
    private readonly IJSRuntime _js;

    public LocalStorageService(IJSRuntime js)
    {
        _js = js;
    }

    public async Task<T?> GetAsync<T>(string key)
    {
        var raw = await _js.InvokeAsync<string?>("localStorage.getItem", key);
        if (string.IsNullOrEmpty(raw)) return default;
        try { return JsonSerializer.Deserialize<T>(raw); }
        catch { return default; }
    }

    public Task SetAsync<T>(string key, T value)
    {
        var json = JsonSerializer.Serialize(value);
        return _js.InvokeVoidAsync("localStorage.setItem", key, json).AsTask();
    }

    public Task RemoveAsync(string key)
        => _js.InvokeVoidAsync("localStorage.removeItem", key).AsTask();

    public async Task<string?> GetRawAsync(string key)
        => await _js.InvokeAsync<string?>("localStorage.getItem", key);

    public Task SetRawAsync(string key, string value)
        => _js.InvokeVoidAsync("localStorage.setItem", key, value).AsTask();

    /// <summary>
    /// Get sincrono per i chiamanti sul main thread WASM. Restituisce null fuori da WASM (es. nei test).
    /// </summary>
    public string? GetRaw(string key)
    {
        if (_js is IJSInProcessRuntime sync)
            return sync.Invoke<string?>("localStorage.getItem", key);
        return null;
    }

    public void SetRaw(string key, string value)
    {
        if (_js is IJSInProcessRuntime sync)
            sync.InvokeVoid("localStorage.setItem", key, value);
    }
}
