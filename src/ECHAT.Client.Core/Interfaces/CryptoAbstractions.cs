namespace ECHAT.Client.Core.Interfaces;

// Primitivi crittografici sostituibili via DI.
// IAeadCipher e ISigner hanno UNA sola implementazione reale: WebCrypto (JsAeadCipher / JsSigner),
// testata in JS con Node (tests/js). WebCrypto non gira nell'host xUnit, quindi i test C# usano
// fake che fanno round-trip (verificano il flusso, non il cifrario).
// ICompressor (gzip) è l'unico primitivo che gira in C# anche in produzione.

public interface IAeadCipher
{
    (byte[] ciphertext, byte[] nonce) Encrypt(byte[] plaintext, byte[] key, byte[] aad);
    byte[] Decrypt(byte[] ciphertext, byte[] nonce, byte[] key, byte[] aad);

    /// <summary>
    /// Cede il thread tra un chunk e l'altro così un encrypt lungo non blocca la UI.
    /// L'implementazione di default chiama <see cref="Encrypt"/> in modo sincrono; i cipher
    /// di produzione (WebCrypto) fanno l'override perché async-only.
    /// </summary>
    Task<(byte[] ciphertext, byte[] nonce)> EncryptAsync(
        byte[] plaintext, byte[] key, byte[] aad, IProgress<int>? progress = null)
    {
        var result = Encrypt(plaintext, key, aad);
        progress?.Report(100);
        return Task.FromResult(result);
    }

    /// <summary>
    /// Variante async di <see cref="Decrypt"/>. L'implementazione di default richiama la versione
    /// sincrona; i cipher backed-by-JS (browser) la fanno in override perché WebCrypto è async-only.
    /// </summary>
    Task<byte[]> DecryptAsync(byte[] ciphertext, byte[] nonce, byte[] key, byte[] aad)
        => Task.FromResult(Decrypt(ciphertext, nonce, key, aad));
}

public interface ISigner
{
    byte[] Sign(byte[] data, byte[] privateKey);
    bool Verify(byte[] data, byte[] signature, byte[] publicKey);

    /// <summary>
    /// Varianti async di <see cref="Sign"/>/<see cref="Verify"/>. Default sincrono (impl C# nei test);
    /// la firma di produzione gira su WebCrypto (async-only) e fa l'override.
    /// </summary>
    Task<byte[]> SignAsync(byte[] data, byte[] privateKey) => Task.FromResult(Sign(data, privateKey));
    Task<bool> VerifyAsync(byte[] data, byte[] signature, byte[] publicKey) => Task.FromResult(Verify(data, signature, publicKey));
}

public interface ICompressor
{
    byte[] Compress(byte[] data);
    byte[] Decompress(byte[] data);
    bool ShouldCompress(string? mimeType, long size);
}
