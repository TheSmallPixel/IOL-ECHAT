using System.Security.Cryptography;
using ECHAT.Server.Core.Services;
using FluentAssertions;

namespace ECHAT.Server.Core.Tests;

/// <summary>
/// Prova esplicita del fail-closed di <see cref="EcdsaVerifier.VerifyP1363"/> su ogni argomento
/// null/vuoto indipendentemente. Mutation testing aveva mostrato che i singoli rami della guardia
/// d'ingresso (spki / signedData / signature null o vuoti) non erano asseriti separatamente: si poteva
/// invertire un &amp;&amp;/|| senza far fallire un test. Verifichiamo che ognuno  false, MAI un'eccezione.
/// </summary>
public class EcdsaVerifierInputTests
{
    private static (byte[] spki, byte[] data, byte[] sig) ValidTriple()
    {
        using var ec = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var spki = ec.ExportSubjectPublicKeyInfo();
        var data = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();
        var sig = ec.SignData(data, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        return (spki, data, sig);
    }

    [Fact]
    public void ValidTriple_Verifies()
    {
        var (spki, data, sig) = ValidTriple();
        EcdsaVerifier.VerifyP1363(spki, data, sig).Should().BeTrue();
    }

    public enum Arg { Spki, Data, Sig }

    [Theory]
    [InlineData(Arg.Spki, true)]   // null spki
    [InlineData(Arg.Spki, false)]  // empty spki
    [InlineData(Arg.Data, true)]
    [InlineData(Arg.Data, false)]
    [InlineData(Arg.Sig, true)]
    [InlineData(Arg.Sig, false)]
    public void NullOrEmptyArgument_FailsClosed_DoesNotThrow(Arg which, bool useNull)
    {
        var (spki, data, sig) = ValidTriple();
        byte[]? Mangle(byte[] valid) => useNull ? null : Array.Empty<byte>();

        var s = which == Arg.Spki ? Mangle(spki)! : spki;
        var d = which == Arg.Data ? Mangle(data)! : data;
        var g = which == Arg.Sig ? Mangle(sig)! : sig;

        // Must return false and never throw, regardless of which input is missing.
        Func<bool> act = () => EcdsaVerifier.VerifyP1363(s, d, g);
        act.Should().NotThrow().Which.Should().BeFalse();
    }
}
