using System.Security.Cryptography;
using ECHAT.Client.Core.Interfaces;
using Microsoft.JSInterop;

namespace ECHAT.Client.App.Services;

/// <summary>
/// AEAD di produzione (browser): AES-GCM via WebCrypto (<c>window.echatCrypto</c>  <c>crypto-worker.js</c>
///  <c>echat-crypto.mjs</c>), fuori dal main thread. Wire format: 0xA1 | IV(12) | (ciphertext || tag).
/// È l'unica implementazione AEAD: la correttezza del cifrario è verificata dai test JS (Node).
///
/// I metodi sincroni lanciano: WebCrypto è async-only, i chiamati devono usare le varianti Async.
/// </summary>
public sealed class JsAeadCipher : IAeadCipher
{
    private const byte AesGcmV1Magic = 0xA1; // deve combaciare con MAGIC_AES_GCM_V1 in echat-crypto.mjs
    private const int IvOffset = 1;          // dopo il magic byte
    private const int IvLen = 12;

    private readonly IJSRuntime _js;

    public JsAeadCipher(IJSRuntime js) => _js = js;

    public (byte[] ciphertext, byte[] nonce) Encrypt(byte[] plaintext, byte[] key, byte[] aad)
        => throw new NotSupportedException("JsAeadCipher è async-only: usa EncryptAsync.");

    public byte[] Decrypt(byte[] ciphertext, byte[] nonce, byte[] key, byte[] aad)
        => throw new NotSupportedException("JsAeadCipher è async-only: usa DecryptAsync.");

    public async Task<(byte[] ciphertext, byte[] nonce)> EncryptAsync(
        byte[] plaintext, byte[] key, byte[] aad, IProgress<int>? progress = null)
    {
        var combined = await _js.InvokeAsync<byte[]>("echatCrypto.encrypt", plaintext, key, aad);
        progress?.Report(100);

        // L'IV vive dentro `combined` (byte 1..12). Lo estraiamo per popolare envelope.Nonce,
        // ma il decrypt lo rilegge comunque dal blob, quindi è solo informativo/di parità.
        var iv = new byte[IvLen];
        Buffer.BlockCopy(combined, IvOffset, iv, 0, IvLen);
        return (combined, iv);
    }

    public async Task<byte[]> DecryptAsync(byte[] ciphertext, byte[] nonce, byte[] key, byte[] aad)
    {
        if (ciphertext.Length == 0 || ciphertext[0] != AesGcmV1Magic)
            throw new CryptographicException("Not an AES-GCM v1 ciphertext (expected magic 0xA1).");

        return await _js.InvokeAsync<byte[]>("echatCrypto.decrypt", ciphertext, key, aad);
    }
}
