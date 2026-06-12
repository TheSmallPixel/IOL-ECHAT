using System.Security.Cryptography;
using ECHAT.Client.Core.Interfaces;

namespace ECHAT.Integration.Tests.Fakes;

/// <summary>
/// <see cref="IDeviceKeyStore"/> con crypto .NET reale (ECDSA P-256 + RSA-OAEP-SHA256), stessi
/// formati/parametri della produzione WebCrypto (firma P1363 sul digest, wrap RSA-OAEP magic 0xB2).
/// Permette ai test integrazione di esercitare firma/verifica/wrap/unwrap end-to-end in-process.
/// </summary>
public sealed class RealDeviceKeyStore : IDeviceKeyStore, IDisposable
{
    private const byte Magic = 0xB2;
    private readonly ECDsa _ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private readonly RSA _rsa = RSA.Create(2048);

    public Guid DeviceId { get; } = Guid.NewGuid();
    public byte[] RsaSpki => _rsa.ExportSubjectPublicKeyInfo();
    public byte[] EcdsaSpki => _ecdsa.ExportSubjectPublicKeyInfo();

    public Task<DeviceKeys> EnsureDeviceAsync() => Task.FromResult(new DeviceKeys(DeviceId, RsaSpki, EcdsaSpki));
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
        catch { return Task.FromResult(false); }
    }

    public Task<byte[]> WrapCekAsync(byte[] cek, byte[] recipientRsaSpki)
    {
        using var r = RSA.Create();
        r.ImportSubjectPublicKeyInfo(recipientRsaSpki, out _);
        var w = r.Encrypt(cek, RSAEncryptionPadding.OaepSHA256);
        var blob = new byte[1 + w.Length];
        blob[0] = Magic;
        w.CopyTo(blob, 1);
        return Task.FromResult(blob);
    }

    public Task<byte[]> UnwrapCekAsync(byte[] wrappedCek)
        => Task.FromResult(_rsa.Decrypt(wrappedCek[1..], RSAEncryptionPadding.OaepSHA256));

    public void Dispose()
    {
        _ecdsa.Dispose();
        _rsa.Dispose();
    }
}
