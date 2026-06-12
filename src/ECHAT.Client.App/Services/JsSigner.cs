using ECHAT.Client.Core.Interfaces;
using Microsoft.JSInterop;

namespace ECHAT.Client.App.Services;

/// <summary>
/// Firma di produzione (browser): HMAC-SHA256 via WebCrypto (<c>window.echatCrypto</c> 
/// <c>echat-crypto.mjs</c>). Unica implementazione reale della firma (verificata dai test JS).
/// Async-only: i metodi sincroni lanciano.
/// </summary>
public sealed class JsSigner : ISigner
{
    private readonly IJSRuntime _js;

    public JsSigner(IJSRuntime js) => _js = js;

    public byte[] Sign(byte[] data, byte[] privateKey)
        => throw new NotSupportedException("JsSigner è async-only: usa SignAsync.");

    public bool Verify(byte[] data, byte[] signature, byte[] publicKey)
        => throw new NotSupportedException("JsSigner è async-only: usa VerifyAsync.");

    public async Task<byte[]> SignAsync(byte[] data, byte[] privateKey)
        => await _js.InvokeAsync<byte[]>("echatCrypto.sign", data, privateKey);

    public async Task<bool> VerifyAsync(byte[] data, byte[] signature, byte[] publicKey)
        => await _js.InvokeAsync<bool>("echatCrypto.verify", data, signature, publicKey);
}
