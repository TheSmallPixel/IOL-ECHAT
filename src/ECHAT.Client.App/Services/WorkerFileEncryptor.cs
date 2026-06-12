using ECHAT.Client.Core.Interfaces;
using Microsoft.JSInterop;

namespace ECHAT.Client.App.Services;

/// <summary>
/// Wrapper JSInterop sul Web Worker AES-GCM (<c>js/crypto-worker.js</c>).
/// Tutta la crypto gira nel thread del worker, così cifrare un file da 50 MiB non blocca più la UI.
/// </summary>
public class WorkerFileEncryptor : IFileBodyCipher
{
    public const byte AesGcmV1Magic = 0xA1;

    byte IFileBodyCipher.AesGcmV1Magic => AesGcmV1Magic;

    private static readonly byte[] FileAad = System.Text.Encoding.UTF8.GetBytes("echat-file-v1");

    private readonly IJSRuntime _js;

    public WorkerFileEncryptor(IJSRuntime js)
    {
        _js = js;
    }

    /// <summary>
    /// Cifra il payload del file con AES-GCM nel Web Worker.
    /// Restituisce un buffer con layout: 0xA1 | IV(12) | (ciphertext || authTag(16)).
    /// </summary>
    public Task<byte[]> EncryptAsync(byte[] plaintext, byte[] key)
        => _js.InvokeAsync<byte[]>("echatCrypto.encrypt", plaintext, key, FileAad).AsTask();

    /// <summary>
    /// Decifra un ciphertext AES-GCM v1 (il buffer deve iniziare col magic byte).
    /// Si assume che il chiamante abbia già validato il magic byte; un magic mancante o errato
    /// indica formato corrotto o non supportato e va rifiutato a monte.
    /// </summary>
    public Task<byte[]> DecryptAsync(byte[] ciphertext, byte[] key)
        => _js.InvokeAsync<byte[]>("echatCrypto.decrypt", ciphertext, key, FileAad).AsTask();
}
