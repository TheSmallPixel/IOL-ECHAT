using System.Security.Cryptography;
using ECHAT.Client.Core.Interfaces;

namespace ECHAT.Integration.Tests.Fakes;

/// <summary>
/// <see cref="IAeadCipher"/> fake per i test: XOR round-trip con nonce (12 byte) in testa.
/// NON è crittografia reale: verifica solo round-trip/flow. La correttezza AES-GCM è coperta
/// dai test JS in <c>tests/js</c> (che esercitano il codice di produzione).
/// </summary>
public sealed class FakeAead : IAeadCipher
{
    public (byte[] ciphertext, byte[] nonce) Encrypt(byte[] plaintext, byte[] key, byte[] aad)
    {
        var nonce = RandomNumberGenerator.GetBytes(12);
        var ct = Xor(plaintext, key);
        var combined = new byte[12 + ct.Length];
        Buffer.BlockCopy(nonce, 0, combined, 0, 12);
        Buffer.BlockCopy(ct, 0, combined, 12, ct.Length);
        return (combined, nonce);
    }

    public byte[] Decrypt(byte[] combined, byte[] nonce, byte[] key, byte[] aad)
    {
        var ct = new byte[combined.Length - 12];
        Buffer.BlockCopy(combined, 12, ct, 0, ct.Length);
        return Xor(ct, key);
    }

    private static byte[] Xor(byte[] data, byte[] key)
    {
        var r = new byte[data.Length];
        for (int i = 0; i < data.Length; i++) r[i] = (byte)(data[i] ^ key[i % key.Length]);
        return r;
    }
}
