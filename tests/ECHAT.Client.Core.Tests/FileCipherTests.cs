using System.Security.Cryptography;
using ECHAT.Client.Core.Interfaces;
using ECHAT.Client.Core.Services;
using FluentAssertions;

namespace ECHAT.Client.Core.Tests;

public class FileCipherTests
{
    /// <summary>
    /// AEAD di riferimento che ricalca il layout di produzione HMAC-CTR + Encrypt-then-MAC
    /// (IV(16) || ciphertext || tag(32)). Così testiamo il cipher end-to-end senza tirare
    /// dentro Client.App.
    /// </summary>
    private class TestAead : IAeadCipher
    {
        public (byte[] ciphertext, byte[] nonce) Encrypt(byte[] plaintext, byte[] key, byte[] aad)
        {
            var iv = RandomNumberGenerator.GetBytes(16);
            var ct = Xor(plaintext, key);
            using var hmac = new HMACSHA256(key);
            hmac.TransformBlock(aad, 0, aad.Length, null, 0);
            hmac.TransformBlock(iv, 0, iv.Length, null, 0);
            hmac.TransformFinalBlock(ct, 0, ct.Length);
            var tag = hmac.Hash!;
            var combined = new byte[16 + ct.Length + 32];
            Buffer.BlockCopy(iv, 0, combined, 0, 16);
            Buffer.BlockCopy(ct, 0, combined, 16, ct.Length);
            Buffer.BlockCopy(tag, 0, combined, 16 + ct.Length, 32);
            return (combined, iv);
        }

        public byte[] Decrypt(byte[] combined, byte[] nonce, byte[] key, byte[] aad)
        {
            var iv = new byte[16];
            var tag = new byte[32];
            var ct = new byte[combined.Length - 16 - 32];
            Buffer.BlockCopy(combined, 0, iv, 0, 16);
            Buffer.BlockCopy(combined, 16, ct, 0, ct.Length);
            Buffer.BlockCopy(combined, 16 + ct.Length, tag, 0, 32);

            using var hmac = new HMACSHA256(key);
            hmac.TransformBlock(aad, 0, aad.Length, null, 0);
            hmac.TransformBlock(iv, 0, iv.Length, null, 0);
            hmac.TransformFinalBlock(ct, 0, ct.Length);
            if (!CryptographicOperations.FixedTimeEquals(tag, hmac.Hash!))
                throw new CryptographicException("MAC failed");

            return Xor(ct, key);
        }

        private static byte[] Xor(byte[] data, byte[] key)
        {
            var result = new byte[data.Length];
            for (int i = 0; i < data.Length; i++) result[i] = (byte)(data[i] ^ key[i % key.Length]);
            return result;
        }
    }

    private static FileCipher Sut() => new(new TestAead());

    [Fact]
    public async Task WrapKey_UnwrapKey_RoundTrips()
    {
        // Il corpo file cifrato dal worker porta con sé una DEK fresca; lato C# la wrappiamo
        // sotto la CEK della conversation per il trasporto.
        var cek = RandomNumberGenerator.GetBytes(32);
        var dek = RandomNumberGenerator.GetBytes(32);
        var sut = Sut();

        var wrapped = await sut.WrapKeyAsync(dek, cek);
        var unwrapped = await sut.UnwrapKeyAsync(wrapped, cek);

        unwrapped.Should().Equal(dek);
    }

    [Fact]
    public async Task WrapKey_FreshIvPerCall_SoSameDekProducesDifferentWrap()
    {
        var cek = RandomNumberGenerator.GetBytes(32);
        var dek = new byte[32];
        var sut = Sut();

        var a = await sut.WrapKeyAsync(dek, cek);
        var b = await sut.WrapKeyAsync(dek, cek);

        a.Should().NotEqual(b, "every wrap uses a fresh nonce; same DEK must never produce the same wrap");
    }

    [Theory]
    [InlineData(16)]
    [InlineData(33)]
    [InlineData(64)]
    public async Task WrapKey_RejectsWrongDekLength(int length)
    {
        var cek = RandomNumberGenerator.GetBytes(32);
        var act = () => Sut().WrapKeyAsync(new byte[length], cek);
        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task WrapKey_RejectsWrongCekLength()
    {
        var act = () => Sut().WrapKeyAsync(new byte[32], new byte[16]);
        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task UnwrapKey_RejectsWrongCekLength()
    {
        var act = () => Sut().UnwrapKeyAsync(new byte[64], new byte[16]);
        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task UnwrapKey_WrongCek_Throws()
    {
        var sut = Sut();
        var wrapped = await sut.WrapKeyAsync(new byte[32], RandomNumberGenerator.GetBytes(32));

        var act = () => sut.UnwrapKeyAsync(wrapped, RandomNumberGenerator.GetBytes(32));

        await act.Should().ThrowAsync<CryptographicException>();
    }
}
