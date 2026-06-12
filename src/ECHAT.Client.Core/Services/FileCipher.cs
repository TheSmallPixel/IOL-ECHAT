using System.Text;
using ECHAT.Client.Core.Interfaces;

namespace ECHAT.Client.Core.Services;

/// <summary>
/// Wrap e unwrap della DEK (Data Encryption Key) per-file sotto la CEK (Conversation Encryption Key).
/// Usa lo stesso <see cref="IAeadCipher"/> AES-GCM del resto della pipeline (in produzione backed-by
/// WebCrypto, async-only). La DEK wrappata viaggia in <c>AttachmentRef.WrappedFileDek</c>.
/// </summary>
public class FileCipher
{
    private static readonly byte[] DekAad = Encoding.UTF8.GetBytes("echat-dek-v1");

    private readonly IAeadCipher _aead;

    public FileCipher(IAeadCipher aead)
    {
        _aead = aead;
    }

    /// <summary>Wrappa una DEK da 32 byte sotto la CEK della conversazione.</summary>
    public async Task<byte[]> WrapKeyAsync(byte[] dek, byte[] cek)
    {
        if (cek.Length != 32) throw new ArgumentException("CEK must be 32 bytes", nameof(cek));
        if (dek.Length != 32) throw new ArgumentException("DEK must be 32 bytes", nameof(dek));
        var (wrapped, _) = await _aead.EncryptAsync(dek, cek, DekAad);
        return wrapped;
    }

    /// <summary>Unwrappa una DEK usando la CEK della conversazione.</summary>
    public async Task<byte[]> UnwrapKeyAsync(byte[] wrappedDek, byte[] cek)
    {
        if (cek.Length != 32) throw new ArgumentException("CEK must be 32 bytes", nameof(cek));
        return await _aead.DecryptAsync(wrappedDek, Array.Empty<byte>(), cek, DekAad);
    }
}
