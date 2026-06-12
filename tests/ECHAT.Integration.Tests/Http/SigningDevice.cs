using System.Security.Cryptography;
using ECHAT.Models.Domain;

namespace ECHAT.Integration.Tests.Http;

/// <summary>
/// Device mittente con crypto .NET reale per i test E2E: keypair ECDSA P-256 (firma) + RSA-OAEP-2048
/// (wrap CEK), stessi formati/parametri della produzione WebCrypto. Firma il digest di
/// <see cref="EnvelopeHasher.Compute"/> in formato IEEE-P1363 (concatenazione a campo fisso),
/// esattamente come <c>RealDeviceKeyStore</c> e come verifica <c>EcdsaVerifier.VerifyP1363</c>.
/// </summary>
public sealed class SigningDevice : IDisposable
{
    private readonly ECDsa _ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private readonly RSA _rsa = RSA.Create(2048);

    public Guid DeviceId { get; } = Guid.NewGuid();
    public byte[] EcdsaSpki => _ecdsa.ExportSubjectPublicKeyInfo();
    public byte[] RsaOaepSpki => _rsa.ExportSubjectPublicKeyInfo();

    /// <summary>Firma il digest dell'envelope (S3). Il digest include conv/msg/seq/epoch/device/nonce/ciphertext.</summary>
    public byte[] Sign(MessageEnvelope envelope)
    {
        var hash = EnvelopeHasher.Compute(envelope);
        return _ecdsa.SignData(hash, HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
    }

    /// <summary>
    /// Wrappa una CEK con la chiave pubblica RSA di questo device: <c>0xB2 ‖ RSA-OAEP-SHA256(CEK)</c>.
    /// Per una CEK da 32 byte produce esattamente 257 byte (1 magic + 256 ciphertext), il formato che
    /// KeyAccessService.StoreClientWrapsAsync accetta (v1).
    /// </summary>
    public byte[] WrapCek(byte[] cek)
    {
        var ct = _rsa.Encrypt(cek, RSAEncryptionPadding.OaepSHA256);
        var blob = new byte[1 + ct.Length];
        blob[0] = 0xB2;
        ct.CopyTo(blob, 1);
        return blob;
    }

    public void Dispose()
    {
        _ecdsa.Dispose();
        _rsa.Dispose();
    }
}
