using System.Security.Cryptography;
using ECHAT.Client.Core.Interfaces;

namespace ECHAT.Client.Core.Tests.Fakes;

/// <summary>
/// Fake di <see cref="IDeviceKeyStore"/> che usa crypto .NET REALE (ECDSA P-256 + RSA-OAEP-SHA256),
/// con gli stessi parametri/formati della produzione WebCrypto: firma P1363 sul digest, wrap RSA-OAEP
/// con magic 0xB2. Così i test esercitano davvero firma/verifica/wrap/unwrap (non un round-trip finto)
/// e restano deterministici/in-process. Espone le chiavi pubbliche per popolare la directory nei test.
/// </summary>
public sealed class FakeDeviceKeyStore : IDeviceKeyStore, IDisposable
{
    private const byte MagicRsaWrap = 0xB2;

    private readonly ECDsa _ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private readonly RSA _rsa = RSA.Create(2048);

    public Guid DeviceId { get; } = Guid.NewGuid();
    public byte[] EcdsaSpki => _ecdsa.ExportSubjectPublicKeyInfo();
    public byte[] RsaSpki => _rsa.ExportSubjectPublicKeyInfo();

    public Task<DeviceKeys> EnsureDeviceAsync()
        => Task.FromResult(new DeviceKeys(DeviceId, RsaSpki, EcdsaSpki));

    public Task<Guid> GetDeviceIdAsync() => Task.FromResult(DeviceId);

    public Task<byte[]> SignHashAsync(byte[] hash)
        => Task.FromResult(_ecdsa.SignData(hash, HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation));

    public Task<bool> VerifySignatureAsync(byte[] hash, byte[] signature, byte[] signerEcdsaSpki)
    {
        try
        {
            using var v = ECDsa.Create();
            v.ImportSubjectPublicKeyInfo(signerEcdsaSpki, out _);
            return Task.FromResult(v.VerifyData(hash, signature, HashAlgorithmName.SHA256,
                DSASignatureFormat.IeeeP1363FixedFieldConcatenation));
        }
        catch
        {
            return Task.FromResult(false);
        }
    }

    public Task<byte[]> WrapCekAsync(byte[] cek, byte[] recipientRsaSpki)
    {
        using var r = RSA.Create();
        r.ImportSubjectPublicKeyInfo(recipientRsaSpki, out _);
        var wrapped = r.Encrypt(cek, RSAEncryptionPadding.OaepSHA256);
        var blob = new byte[1 + wrapped.Length];
        blob[0] = MagicRsaWrap;
        wrapped.CopyTo(blob, 1);
        return Task.FromResult(blob);
    }

    public Task<byte[]> UnwrapCekAsync(byte[] wrappedCek)
    {
        if (wrappedCek.Length < 1 || wrappedCek[0] != MagicRsaWrap)
            throw new CryptographicException("Not an RSA-OAEP v1 wrapped CEK (expected magic 0xB2).");
        return Task.FromResult(_rsa.Decrypt(wrappedCek[1..], RSAEncryptionPadding.OaepSHA256));
    }

    public void Dispose()
    {
        _ecdsa.Dispose();
        _rsa.Dispose();
    }
}
