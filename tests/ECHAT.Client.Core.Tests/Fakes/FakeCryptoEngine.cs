using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ECHAT.Client.Core.Interfaces;
using ECHAT.Models.Domain;
using ECHAT.Models.Dtos;

namespace ECHAT.Client.Core.Tests.Fakes;

/// <summary>
/// Crypto engine deterministico per i test: JSON + XOR col primo byte della cek, niente blocchi
/// di compressione o tag MAC. Sufficiente per verificare il flusso (encrypt  decrypt round-trip,
/// chain hash, firma).
/// </summary>
public class FakeCryptoEngine : ICryptoEngine
{
    public Task<EncryptResult> EncryptAsync(MessagePayload payload, byte[] cek, Guid conversationId, Guid messageId, long seq, int epochId)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(payload);
        var nonce = RandomNumberGenerator.GetBytes(12);
        var ct = new byte[json.Length];
        var k = cek.Length > 0 ? cek[0] : (byte)0x42;
        for (int i = 0; i < json.Length; i++) ct[i] = (byte)(json[i] ^ k);
        return Task.FromResult(new EncryptResult { Ciphertext = ct, Nonce = nonce });
    }

    public Task<MessagePayload> DecryptAsync(byte[] ciphertext, byte[] nonce, byte[] cek, Guid conversationId, Guid messageId, long seq, int epochId)
    {
        var k = cek.Length > 0 ? cek[0] : (byte)0x42;
        var json = new byte[ciphertext.Length];
        for (int i = 0; i < ciphertext.Length; i++) json[i] = (byte)(ciphertext[i] ^ k);
        return Task.FromResult(JsonSerializer.Deserialize<MessagePayload>(json)!);
    }

    public Task<byte[]> SignAsync(byte[] data, byte[] privateKey)
        => Task.FromResult(SHA256.HashData(data.Concat(privateKey).ToArray()));

    public Task<bool> VerifyAsync(byte[] data, byte[] signature, byte[] publicKey)
        => Task.FromResult(SHA256.HashData(data.Concat(publicKey).ToArray()).SequenceEqual(signature));

    // L'hash dell'envelope è SHA-256 deterministico condiviso col server: non va finto, altrimenti
    // diverge da quello che ChainValidator/EnvelopeHasher calcolano davvero. Delego al vero hasher.
    public byte[] ComputeEnvelopeHash(MessageEnvelope envelope) => EnvelopeHasher.Compute(envelope);
}
