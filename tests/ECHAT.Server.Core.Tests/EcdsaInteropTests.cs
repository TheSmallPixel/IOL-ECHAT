using ECHAT.Server.Core.Services;
using FluentAssertions;

namespace ECHAT.Server.Core.Tests;

/// <summary>
/// Pin del punto di interop più rischioso del redesign E2EE: una firma ECDSA P-256 prodotta da
/// WebCrypto (browser/Node, formato IEEE-P1363 r‖s di 64 byte) DEVE verificare sotto .NET
/// <c>ECDsa.VerifyHash(..., DSASignatureFormat.IeeeP1363)</c>. Il vettore qui sotto è stato generato
/// con il vero <c>echat-crypto.mjs</c> (signEcdsa) su Node 20 WebCrypto, sul digest [0x00..0x1F].
/// Se questo test fallisce, client e server non sono d'accordo sulle firme e tutta la verifica salta.
/// </summary>
public class EcdsaInteropTests
{
    // Vettore prodotto da WebCrypto (Node); vedi tests/js per il codice di generazione.
    private const string SpkiB64 =
        "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEmYl33yqGYJ77PKYcjJXhsxLOu2vgsCXmOjp3p5vV8Ip0FldRGrJ1R2uxRGj3wv5PII5hYdt2bv7A2wLgMWHC3g==";
    private const string SigB64 =
        "1+hw5w0b/4oj7tUslUM6bFPF5HZDxWyLPVnFK3n0x0xVNFEywwWGzL4zE/4IQSeyw7sbLCXDl9J4lR8SEsxt/A==";

    private static byte[] Hash() => Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();

    [Fact]
    public void WebCryptoEcdsaSignature_VerifiesUnderDotNet_P1363()
    {
        var spki = Convert.FromBase64String(SpkiB64);
        var sig = Convert.FromBase64String(SigB64);

        sig.Length.Should().Be(64, "WebCrypto emette ECDSA P-256 in P1363 (r‖s), non DER");
        EcdsaVerifier.VerifyP1363(spki, Hash(), sig).Should().BeTrue();
    }

    [Fact]
    public void TamperedHash_FailsVerification()
    {
        var spki = Convert.FromBase64String(SpkiB64);
        var sig = Convert.FromBase64String(SigB64);
        var hash = Hash();
        hash[0] ^= 0xFF;

        EcdsaVerifier.VerifyP1363(spki, hash, sig).Should().BeFalse();
    }

    [Fact]
    public void TamperedSignature_FailsVerification()
    {
        var spki = Convert.FromBase64String(SpkiB64);
        var sig = Convert.FromBase64String(SigB64);
        sig[10] ^= 0xFF;

        EcdsaVerifier.VerifyP1363(spki, Hash(), sig).Should().BeFalse();
    }

    [Fact]
    public void Garbage_FailsClosed_NotThrow()
    {
        EcdsaVerifier.VerifyP1363(new byte[] { 1, 2, 3 }, Hash(), new byte[64]).Should().BeFalse();
        EcdsaVerifier.VerifyP1363(Array.Empty<byte>(), Hash(), new byte[64]).Should().BeFalse();
    }
}
