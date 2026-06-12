using ECHAT.Client.Core.Interfaces;

namespace ECHAT.Client.Core.Tests.Fakes;

/// <summary>
/// Fake del cifratore del corpo file. Produce un buffer roundtrippabile col layout reale:
/// 0xA1 | key(32) | plaintext. La decifratura ricontrolla magic + chiave e restituisce il
/// plaintext, così i test verificano che la DEK corretta sia stata passata.
/// </summary>
public class FakeFileBodyCipher : IFileBodyCipher
{
    public byte AesGcmV1Magic => 0xA1;

    public Task<byte[]> EncryptAsync(byte[] plaintext, byte[] key)
    {
        var buffer = new byte[1 + key.Length + plaintext.Length];
        buffer[0] = AesGcmV1Magic;
        Array.Copy(key, 0, buffer, 1, key.Length);
        Array.Copy(plaintext, 0, buffer, 1 + key.Length, plaintext.Length);
        return Task.FromResult(buffer);
    }

    public Task<byte[]> DecryptAsync(byte[] ciphertext, byte[] key)
    {
        if (ciphertext.Length < 1 + 32 || ciphertext[0] != AesGcmV1Magic)
            throw new InvalidOperationException("Bad ciphertext");
        var embeddedKey = ciphertext.Skip(1).Take(key.Length).ToArray();
        if (!embeddedKey.SequenceEqual(key))
            throw new InvalidOperationException("DEK mismatch");
        return Task.FromResult(ciphertext.Skip(1 + key.Length).ToArray());
    }
}
