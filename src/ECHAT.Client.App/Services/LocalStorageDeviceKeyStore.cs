using ECHAT.Client.Core.Interfaces;
using Microsoft.JSInterop;

namespace ECHAT.Client.App.Services;

/// <summary>
/// Implementazione di produzione di <see cref="IDeviceKeyStore"/> sopra WebCrypto + IndexedDB
/// (E2EE redesign). Le coppie di chiavi del device (RSA-OAEP-2048 per il wrap della CEK, ECDSA P-256
/// per le firme) sono <c>CryptoKey</c> NON estraibili custodite in IndexedDB da <c>echat.js</c>;
/// questa classe è solo un bridge che instrada le chiamate a <c>window.echatCrypto.*</c>. I byte
/// privati non passano mai per C#. Il naming "LocalStorage..." è storico (ora usa IndexedDB).
/// </summary>
public class LocalStorageDeviceKeyStore : IDeviceKeyStore
{
    private readonly IJSRuntime _js;
    private DeviceKeys? _cached;

    public LocalStorageDeviceKeyStore(IJSRuntime js)
    {
        _js = js;
    }

    public async Task<DeviceKeys> EnsureDeviceAsync()
    {
        if (_cached is not null) return _cached;
        // echat.js genera (se assenti) e persiste le coppie in IndexedDB, e ritorna id + SPKI base64.
        var info = await _js.InvokeAsync<DeviceKeyInfo>("echatCrypto.ensureDevice");
        _cached = new DeviceKeys(
            Guid.Parse(info.DeviceId),
            Convert.FromBase64String(info.RsaSpkiB64),
            Convert.FromBase64String(info.EcdsaSpkiB64));
        return _cached;
    }

    public async Task<Guid> GetDeviceIdAsync() => (await EnsureDeviceAsync()).DeviceId;

    public Task<byte[]> SignHashAsync(byte[] hash)
        => _js.InvokeAsync<byte[]>("echatCrypto.signHash", hash).AsTask();

    public Task<bool> VerifySignatureAsync(byte[] hash, byte[] signature, byte[] signerEcdsaSpki)
        => _js.InvokeAsync<bool>("echatCrypto.verifySignature", hash, signature, signerEcdsaSpki).AsTask();

    public Task<byte[]> WrapCekAsync(byte[] cek, byte[] recipientRsaSpki)
        => _js.InvokeAsync<byte[]>("echatCrypto.wrapCekFor", cek, recipientRsaSpki).AsTask();

    public Task<byte[]> UnwrapCekAsync(byte[] wrappedCek)
        => _js.InvokeAsync<byte[]>("echatCrypto.unwrapCek", wrappedCek).AsTask();

    private sealed class DeviceKeyInfo
    {
        public string DeviceId { get; set; } = string.Empty;
        public string RsaSpkiB64 { get; set; } = string.Empty;
        public string EcdsaSpkiB64 { get; set; } = string.Empty;
    }
}
