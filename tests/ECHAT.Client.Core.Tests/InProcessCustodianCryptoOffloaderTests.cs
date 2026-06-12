using System.Security.Cryptography;
using ECHAT.Client.Core.Services;
using ECHAT.Client.Core.Tests.Fakes;
using ECHAT.Models.Domain;
using FluentAssertions;

namespace ECHAT.Client.Core.Tests;

public class InProcessCustodianCryptoOffloaderTests
{
    private readonly Guid _conv = Guid.NewGuid();
    private readonly Guid _msg = Guid.NewGuid();

    // CEK distinte da 32 byte (FakeAead/FakeCryptoEngine usano il primo byte come "chiave").
    private readonly byte[] _oldCek = Enumerable.Repeat((byte)0x11, 32).ToArray();
    private readonly byte[] _newCek = Enumerable.Repeat((byte)0x22, 32).ToArray();

    private readonly FakeCryptoEngine _crypto = new();
    private readonly FileCipher _files = new(new FakeAead());
    private readonly FakeDeviceKeyStore _keys = new();

    private InProcessCustodianCryptoOffloader Sut() => new(_crypto, _files, _keys);

    [Fact]
    public async Task EncryptAndSign_RewrapsAttachmentDek_FromOldCekToNewCek()
    {
        var sut = Sut();
        var dek = RandomNumberGenerator.GetBytes(32);
        // La DEK dell'allegato è inizialmente wrappata sotto la VECCHIA CEK.
        var wrappedUnderOld = await _files.WrapKeyAsync(dek, _oldCek);

        var payload = new MessagePayload
        {
            Seq = 7,
            Text = "with attachment",
            Attachments = new List<AttachmentRef>
            {
                new()
                {
                    FileId = Guid.NewGuid(),
                    WrappedFileDek = wrappedUnderOld,
                    FileName = "doc.pdf",
                    MimeType = "application/pdf",
                    Size = 123
                }
            }
        };

        var result = await sut.EncryptAndSignAsync(
            payload, _oldCek, _newCek, _keys.DeviceId,
            _conv, _msg, seq: 7, newEpochId: 2, overridePrevEnvelopeHash: null, CancellationToken.None);

        // Il ciphertext è cifrato con la NUOVA CEK: decifrandolo recuperiamo il payload ri-cifrato.
        var roundtripped = await _crypto.DecryptAsync(
            result.Ciphertext, result.Nonce, _newCek, _conv, _msg, 7, 2);

        roundtripped.Attachments.Should().ContainSingle();
        var newWrapped = roundtripped.Attachments![0].WrappedFileDek;

        // Il wrap è cambiato (ora sotto newCek) ma la DEK unwrappata sotto newCek è la stessa.
        newWrapped.Should().NotEqual(wrappedUnderOld);
        var unwrapped = await _files.UnwrapKeyAsync(newWrapped, _newCek);
        unwrapped.Should().Equal(dek);
    }

    [Fact]
    public async Task EncryptAndSign_OverridePrevEnvelopeHash_ReplacesPrevHashInPayload()
    {
        var sut = Sut();
        var originalPrev = new byte[] { 1, 2, 3 };
        var overrideHash = new byte[] { 9, 9, 9, 9 };

        var payload = new MessagePayload
        {
            Seq = 5,
            Text = "chain link",
            PrevEnvelopeHash = originalPrev
        };

        var result = await sut.EncryptAndSignAsync(
            payload, _oldCek, _newCek, _keys.DeviceId,
            _conv, _msg, seq: 5, newEpochId: 2, overridePrevEnvelopeHash: overrideHash, CancellationToken.None);

        var roundtripped = await _crypto.DecryptAsync(
            result.Ciphertext, result.Nonce, _newCek, _conv, _msg, 5, 2);

        roundtripped.PrevEnvelopeHash.Should().Equal(overrideHash);
        roundtripped.PrevEnvelopeHash.Should().NotEqual(originalPrev);
    }

    [Fact]
    public async Task EncryptAndSign_NoOverrideNoAttachments_KeepsOriginalPrevHash()
    {
        var sut = Sut();
        var originalPrev = new byte[] { 4, 5, 6 };
        var payload = new MessagePayload { Seq = 3, Text = "plain", PrevEnvelopeHash = originalPrev };

        var result = await sut.EncryptAndSignAsync(
            payload, _oldCek, _newCek, _keys.DeviceId,
            _conv, _msg, seq: 3, newEpochId: 2, overridePrevEnvelopeHash: null, CancellationToken.None);

        var roundtripped = await _crypto.DecryptAsync(
            result.Ciphertext, result.Nonce, _newCek, _conv, _msg, 3, 2);

        roundtripped.PrevEnvelopeHash.Should().Equal(originalPrev);
    }

    [Fact]
    public async Task EncryptAndSign_SignatureVerifiesAgainstNewEnvelopeDigest()
    {
        var sut = Sut();
        var payload = new MessagePayload { Seq = 1, Text = "signed" };

        var result = await sut.EncryptAndSignAsync(
            payload, _oldCek, _newCek, _keys.DeviceId,
            _conv, _msg, seq: 1, newEpochId: 2, overridePrevEnvelopeHash: null, CancellationToken.None);

        // Il custode firma il digest del NUOVO envelope (stesso pre-image che il ricevente ricalcola).
        var envelope = new MessageEnvelope
        {
            ConversationId = _conv,
            MessageId = _msg,
            Seq = 1,
            EpochId = 2,
            SenderDeviceId = _keys.DeviceId,
            Nonce = result.Nonce,
            Ciphertext = result.Ciphertext
        };
        var digest = EnvelopeHasher.Compute(envelope);

        var ok = await _keys.VerifySignatureAsync(digest, result.Signature, _keys.EcdsaSpki);
        ok.Should().BeTrue();
    }

    [Fact]
    public async Task TryDecrypt_BadInput_ReturnsNull()
    {
        var sut = Sut();
        // Ciphertext non valido: FakeCryptoEngine deserializza JSON e lancia  l'offloader inghiotte e ritorna null.
        var garbage = new byte[] { 0xFF, 0xEE, 0xDD };

        var result = await sut.TryDecryptAsync(
            garbage, new byte[12], _oldCek, _conv, _msg, seq: 1, oldEpochId: 1, CancellationToken.None);

        result.Should().BeNull();
    }

    [Fact]
    public async Task TryDecrypt_ValidCiphertext_ReturnsPayload()
    {
        var sut = Sut();
        var payload = new MessagePayload { Seq = 9, Text = "hi" };
        var enc = await _crypto.EncryptAsync(payload, _oldCek, _conv, _msg, 9, 1);

        var result = await sut.TryDecryptAsync(
            enc.Ciphertext, enc.Nonce, _oldCek, _conv, _msg, seq: 9, oldEpochId: 1, CancellationToken.None);

        result.Should().NotBeNull();
        result!.Text.Should().Be("hi");
        result.Seq.Should().Be(9);
    }
}
