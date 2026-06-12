using ECHAT.Client.Core.Services;
using ECHAT.Client.Core.Tests.Fakes;
using ECHAT.Models.Domain;
using FluentAssertions;

namespace ECHAT.Client.Core.Tests;

/// <summary>
/// Testa la logica di composizione dell'engine (serialize  gzip  cipher; e l'inverso) e la
/// delega a signer/hasher, usando fake round-trip. La correttezza del cifrario AES-GCM e della
/// firma HMAC è verificata nei test JS (<c>tests/js</c>), che girano sul codice di produzione.
/// </summary>
public class AeadCryptoEngineTests
{
    private readonly AeadCryptoEngine _sut = new(new FakeAead(), new GzipCompressor(), new FakeSigner());
    private readonly byte[] _cek = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();
    private readonly Guid _conv = Guid.NewGuid();
    private readonly Guid _msg = Guid.NewGuid();

    [Fact]
    public async Task EncryptDecrypt_RoundTrip_ReturnsOriginalPayload()
    {
        var payload = new MessagePayload { Seq = 7, Text = "ciao mondo", PrevEnvelopeHash = new byte[] { 1, 2, 3 } };

        var enc = await _sut.EncryptAsync(payload, _cek, _conv, _msg, seq: 7, epochId: 1);
        var dec = await _sut.DecryptAsync(enc.Ciphertext, enc.Nonce, _cek, _conv, _msg, seq: 7, epochId: 1);

        dec.Seq.Should().Be(payload.Seq);
        dec.Text.Should().Be(payload.Text);
    }

    [Fact]
    public async Task Sign_AndVerify_RoundTrip()
    {
        var data = new byte[] { 1, 2, 3, 4 };
        var key = new byte[] { 9, 9, 9 };

        var sig = await _sut.SignAsync(data, key);
        (await _sut.VerifyAsync(data, sig, key)).Should().BeTrue();
    }

    [Fact]
    public async Task Verify_WithDifferentKey_ReturnsFalse()
    {
        var data = new byte[] { 1, 2, 3 };
        var sig = await _sut.SignAsync(data, new byte[] { 1 });

        (await _sut.VerifyAsync(data, sig, new byte[] { 2 })).Should().BeFalse();
    }

    [Fact]
    public void ComputeEnvelopeHash_DelegatesToEnvelopeHasher_Deterministic()
    {
        var env = new MessageEnvelope
        {
            ConversationId = _conv,
            MessageId = _msg,
            Seq = 1,
            EpochId = 1,
            Ciphertext = new byte[] { 1, 2, 3 },
            Nonce = new byte[] { 4, 5 }
        };

        var a = _sut.ComputeEnvelopeHash(env);
        var b = _sut.ComputeEnvelopeHash(env);

        a.Should().BeEquivalentTo(b);
        a.Should().NotBeEmpty();
    }
}
