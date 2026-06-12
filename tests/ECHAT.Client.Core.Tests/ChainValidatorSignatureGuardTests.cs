using ECHAT.Client.Core.Services;
using ECHAT.Client.Core.Tests.Fakes;
using ECHAT.Models.Domain;
using ECHAT.Models.Enums;
using FluentAssertions;

namespace ECHAT.Client.Core.Tests;

/// <summary>
/// Guardie della verifica firma in <see cref="ChainValidator"/> (lato ricevente, S3). Mutation testing
/// aveva mostrato che il composto `senderEcdsaSpki is {Length:>0} &amp;&amp; Signature.Length>0 &amp;&amp; verify(...)`
/// non era asserito ai bordi: serve che ENTRAMBE le pre-condizioni (SPKI presente E firma non vuota)
/// siano richieste prima di considerare valido il MAC. Senza queste, un envelope senza firma o senza
/// chiave del mittente potrebbe risultare "verificato".
/// </summary>
public class ChainValidatorSignatureGuardTests
{
    private readonly FakeDeviceKeyStore _keys = new();

    private async Task<MessageEnvelope> SignedAsync(byte[] signatureOverride)
    {
        var conv = Guid.NewGuid();
        var msg = Guid.NewGuid();
        MessageEnvelope Build(byte[] sig) => new()
        {
            ConversationId = conv,
            MessageId = msg,
            Seq = 1,
            EpochId = 1,
            SenderDeviceId = _keys.DeviceId,
            Nonce = new byte[] { 1 },
            Ciphertext = new byte[] { 2 },
            Signature = sig,
            Type = MessageType.Text
        };
        // a genuinely valid signature (used when signatureOverride is null)
        var validSig = await _keys.SignHashAsync(EnvelopeHasher.Compute(Build(Array.Empty<byte>())));
        return Build(signatureOverride ?? validSig);
    }

    private static MessagePayload Payload() => new() { Seq = 1, PrevEnvelopeHash = Array.Empty<byte>() };

    [Fact]
    public async Task ValidSpki_ButEmptySignature_MacInvalid()
    {
        var env = await SignedAsync(signatureOverride: Array.Empty<byte>()); // no signature bytes
        var r = await new ChainValidator(_keys).ValidateAsync(env, Payload(), Array.Empty<byte>(), _keys.EcdsaSpki, decryptionSucceeded: true);
        r.MacValid.Should().BeFalse("a zero-length signature must never verify even with a valid SPKI");
        r.IsValid.Should().BeFalse();
        r.Error.Should().Be(ChainError.SignatureInvalid);
    }

    [Fact]
    public async Task EmptySpki_WithValidSignature_MacInvalid()
    {
        var env = await SignedAsync(signatureOverride: null); // genuine signature present
        var r = await new ChainValidator(_keys).ValidateAsync(env, Payload(), Array.Empty<byte>(), senderEcdsaSpki: Array.Empty<byte>(), decryptionSucceeded: true);
        r.MacValid.Should().BeFalse("an empty sender SPKI must never satisfy the MAC check");
        r.IsValid.Should().BeFalse();
    }

    [Fact]
    public async Task ValidSpki_AndValidSignature_MacValid()
    {
        var env = await SignedAsync(signatureOverride: null);
        var r = await new ChainValidator(_keys).ValidateAsync(env, Payload(), Array.Empty<byte>(), _keys.EcdsaSpki, decryptionSucceeded: true);
        r.MacValid.Should().BeTrue();
        r.IsValid.Should().BeTrue();
    }
}
