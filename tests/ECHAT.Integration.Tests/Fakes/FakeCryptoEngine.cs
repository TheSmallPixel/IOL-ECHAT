using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ECHAT.Client.Core.Interfaces;
using ECHAT.Models.Domain;
using ECHAT.Models.Dtos;

namespace ECHAT.Integration.Tests.Fakes;

public class FakeCryptoEngine : ICryptoEngine
{
    public Task<EncryptResult> EncryptAsync(MessagePayload payload, byte[] cek, Guid conversationId, Guid messageId, long seq, int epochId)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(payload);
        var nonce = RandomNumberGenerator.GetBytes(12);
        // Finta "cifratura": XOR col primo byte della cek, solo per testare il roundtrip
        var ciphertext = new byte[json.Length];
        var key = cek.Length > 0 ? cek[0] : (byte)0x42;
        for (int i = 0; i < json.Length; i++)
            ciphertext[i] = (byte)(json[i] ^ key);

        return Task.FromResult(new EncryptResult { Ciphertext = ciphertext, Nonce = nonce });
    }

    public Task<MessagePayload> DecryptAsync(byte[] ciphertext, byte[] nonce, byte[] cek, Guid conversationId, Guid messageId, long seq, int epochId)
    {
        var key = cek.Length > 0 ? cek[0] : (byte)0x42;
        var json = new byte[ciphertext.Length];
        for (int i = 0; i < ciphertext.Length; i++)
            json[i] = (byte)(ciphertext[i] ^ key);

        return Task.FromResult(JsonSerializer.Deserialize<MessagePayload>(json)!);
    }

    public Task<byte[]> SignAsync(byte[] data, byte[] privateKey)
    {
        return Task.FromResult(SHA256.HashData(data.Concat(privateKey).ToArray()));
    }

    public Task<bool> VerifyAsync(byte[] data, byte[] signature, byte[] publicKey)
    {
        var expected = SHA256.HashData(data.Concat(publicKey).ToArray());
        return Task.FromResult(expected.SequenceEqual(signature));
    }

    // L'hash dell'envelope è SHA-256 deterministico condiviso col server: non va finto, altrimenti
    // diverge da quello che ChainValidator/EnvelopeHasher calcolano davvero. Delego al vero hasher.
    public byte[] ComputeEnvelopeHash(MessageEnvelope envelope) => EnvelopeHasher.Compute(envelope);
}
