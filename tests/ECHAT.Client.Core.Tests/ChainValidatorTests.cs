using ECHAT.Client.Core.Services;
using ECHAT.Client.Core.Tests.Fakes;
using ECHAT.Models.Domain;
using ECHAT.Models.Enums;
using FluentAssertions;

namespace ECHAT.Client.Core.Tests;

public class ChainValidatorTests
{
    private readonly FakeDeviceKeyStore _keys = new();

    private ChainValidator CreateValidator() => new(_keys);

    // Costruisce un envelope con id stabili e lo firma con la chiave del fake (così la verifica passa).
    private async Task<MessageEnvelope> SignedEnvelopeAsync(long seq)
    {
        var conv = Guid.NewGuid();
        var msg = Guid.NewGuid();
        var dev = _keys.DeviceId;
        MessageEnvelope Build(byte[] sig) => new()
        {
            ConversationId = conv,
            MessageId = msg,
            Seq = seq,
            EpochId = 1,
            SenderDeviceId = dev,
            Nonce = new byte[] { 1 },
            Ciphertext = new byte[] { 2 },
            Signature = sig,
            LeaseToken = "token",
            Type = MessageType.Text
        };
        var sig = await _keys.SignHashAsync(EnvelopeHasher.Compute(Build(Array.Empty<byte>())));
        return Build(sig);
    }

    [Fact]
    public async Task Validate_MatchingSeqHashAndSignature_ShouldReturnValid()
    {
        var envelope = await SignedEnvelopeAsync(seq: 5);
        var lastHash = new byte[] { 10, 20, 30 };
        var payload = new MessagePayload { Seq = 5, PrevEnvelopeHash = lastHash };

        var result = await CreateValidator().ValidateAsync(envelope, payload, lastHash, _keys.EcdsaSpki, decryptionSucceeded: true);

        result.IsValid.Should().BeTrue();
        result.MacValid.Should().BeTrue();
        result.DecryptionValid.Should().BeTrue();
        result.CurrentEnvelopeHash.Should().BeEquivalentTo(EnvelopeHasher.Compute(envelope));
        result.Error.Should().BeNull();
    }

    [Fact]
    public async Task Validate_SeqMismatch_ShouldReturnInvalid()
    {
        var envelope = await SignedEnvelopeAsync(seq: 5);
        var payload = new MessagePayload { Seq = 6, PrevEnvelopeHash = new byte[] { 10 } };

        var result = await CreateValidator().ValidateAsync(envelope, payload, new byte[] { 10 }, _keys.EcdsaSpki, true);

        result.IsValid.Should().BeFalse();
        result.Error.Should().Be(ChainError.SeqMismatch);
    }

    [Fact]
    public async Task Validate_HashMismatch_ShouldReturnInvalid()
    {
        var envelope = await SignedEnvelopeAsync(seq: 5);
        var payload = new MessagePayload { Seq = 5, PrevEnvelopeHash = new byte[] { 99 } };

        var result = await CreateValidator().ValidateAsync(envelope, payload, new byte[] { 10, 20, 30 }, _keys.EcdsaSpki, true);

        result.IsValid.Should().BeFalse();
        result.Error.Should().Be(ChainError.HashMismatch);
    }

    [Fact]
    public async Task Validate_EmptyLastHash_SkipsHashCheck_StillValidWithGoodSignature()
    {
        var envelope = await SignedEnvelopeAsync(seq: 1);
        var payload = new MessagePayload { Seq = 1, PrevEnvelopeHash = new byte[] { 99 } };

        var result = await CreateValidator().ValidateAsync(envelope, payload, Array.Empty<byte>(), _keys.EcdsaSpki, true);

        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public async Task Validate_BadSignature_ShouldFailWithSignatureInvalid()
    {
        var envelope = await SignedEnvelopeAsync(seq: 5);
        var tampered = (byte[])envelope.Signature.Clone();
        tampered[0] ^= 0xFF;
        var bad = CloneWithSignature(envelope, tampered);
        var payload = new MessagePayload { Seq = 5, PrevEnvelopeHash = Array.Empty<byte>() };

        var result = await CreateValidator().ValidateAsync(bad, payload, Array.Empty<byte>(), _keys.EcdsaSpki, true);

        result.IsValid.Should().BeFalse();
        result.MacValid.Should().BeFalse();
        result.Error.Should().Be(ChainError.SignatureInvalid);
    }

    [Fact]
    public async Task Validate_NoSenderKey_MacInvalid()
    {
        var envelope = await SignedEnvelopeAsync(seq: 5);
        var payload = new MessagePayload { Seq = 5, PrevEnvelopeHash = Array.Empty<byte>() };

        var result = await CreateValidator().ValidateAsync(envelope, payload, Array.Empty<byte>(), senderEcdsaSpki: null, decryptionSucceeded: true);

        result.MacValid.Should().BeFalse();
        result.IsValid.Should().BeFalse();
    }

    [Fact]
    public async Task Validate_DecryptionFailed_NotValid_EvenWithGoodSignature()
    {
        var envelope = await SignedEnvelopeAsync(seq: 5);
        var payload = new MessagePayload { Seq = 5, PrevEnvelopeHash = Array.Empty<byte>() };

        var result = await CreateValidator().ValidateAsync(envelope, payload, Array.Empty<byte>(), _keys.EcdsaSpki, decryptionSucceeded: false);

        result.DecryptionValid.Should().BeFalse();
        result.IsValid.Should().BeFalse();
    }

    private static MessageEnvelope CloneWithSignature(MessageEnvelope e, byte[] sig) => new()
    {
        ConversationId = e.ConversationId,
        MessageId = e.MessageId,
        Seq = e.Seq,
        EpochId = e.EpochId,
        SenderDeviceId = e.SenderDeviceId,
        SenderUserId = e.SenderUserId,
        Nonce = e.Nonce,
        Ciphertext = e.Ciphertext,
        Signature = sig,
        LeaseToken = e.LeaseToken,
        Type = e.Type
    };
}
