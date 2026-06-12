using System.Security.Cryptography;

namespace ECHAT.Server.Core.Services;

/// <summary>
/// Verifica server-side delle firme dei messaggi (S3). Due dettagli di interop con WebCrypto, ENTRAMBI
/// necessari, altrimenti la verifica fallisce sempre:
///   1) formato firma IEEE-P1363 (r‖s, 64 byte), NON DER  <see cref="DSASignatureFormat.IeeeP1363FixedFieldConcatenation"/>;
///   2) WebCrypto <c>sign({hash:'SHA-256'}, key, data)</c> applica SHA-256 al `data` PRIMA di firmare.
///      Il client passa come `data` il digest di <c>EnvelopeHasher</c> (32 byte), quindi WebCrypto firma
///      SHA-256(digest). Lato .NET usiamo perciò <c>VerifyData(..., SHA256, ...)</c> (che ri-hasha),
///      NON <c>VerifyHash</c> (che non ri-hasha). Pinned dal test cross-language EcdsaInteropTests.
/// </summary>
public static class EcdsaVerifier
{
    /// <summary>
    /// Verifica una firma ECDSA P-256 (P1363) prodotta da WebCrypto contro una chiave pubblica SPKI DER.
    /// <paramref name="signedData"/> è il pre-image firmato dal client (il digest di EnvelopeHasher);
    /// SHA-256 viene applicato qui, in parità con WebCrypto. Fail-closed su qualsiasi errore.
    /// </summary>
    public static bool VerifyP1363(byte[] subjectPublicKeyInfo, byte[] signedData, byte[] signatureP1363)
    {
        if (subjectPublicKeyInfo is null || subjectPublicKeyInfo.Length == 0
            || signedData is null || signedData.Length == 0
            || signatureP1363 is null || signatureP1363.Length == 0)
            return false;
        try
        {
            using var ecdsa = ECDsa.Create();
            ecdsa.ImportSubjectPublicKeyInfo(subjectPublicKeyInfo, out _);
            return ecdsa.VerifyData(
                signedData, signatureP1363,
                HashAlgorithmName.SHA256,
                DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        }
        catch
        {
            return false;
        }
    }
}
