using ECHAT.Models.Domain;
using ECHAT.Models.Enums;
using ECHAT.Server.Core.Services;
using FluentAssertions;

namespace ECHAT.Server.Core.Tests;

public class EnvelopeValidatorTests
{
    private readonly EnvelopeValidator _validator = new();

    private static MessageEnvelope Valid(byte[]? ct = null) => new()
    {
        ConversationId = Guid.NewGuid(),
        MessageId = Guid.NewGuid(),
        Seq = 1,
        EpochId = 1,
        SenderDeviceId = Guid.NewGuid(),
        Nonce = new byte[12],
        Ciphertext = ct ?? new byte[] { 1, 2, 3 },
        Signature = new byte[32],
        LeaseToken = "lease",
        Type = MessageType.Text
    };

    [Fact]
    public void Validate_GoodEnvelope_ReturnsNull()
    {
        _validator.Validate(Valid()).Should().BeNull();
    }

    [Fact]
    public void Validate_EmptyCiphertext_Rejected()
    {
        _validator.Validate(Valid(ct: Array.Empty<byte>()))
            .Should().Contain("Ciphertext cannot be empty");
    }

    [Fact]
    public void Validate_OversizedCiphertext_Rejected()
    {
        var huge = new byte[EnvelopeValidator.MaxCiphertextBytes + 1];
        _validator.Validate(Valid(ct: huge)).Should().Contain("exceeds");
    }

    [Fact]
    public void Validate_OversizedNonce_Rejected()
    {
        var env = new MessageEnvelope
        {
            ConversationId = Guid.NewGuid(),
            MessageId = Guid.NewGuid(),
            Seq = 1,
            EpochId = 1,
            Nonce = new byte[EnvelopeValidator.MaxNonceBytes + 1],
            Ciphertext = new byte[] { 1 }
        };
        _validator.Validate(env).Should().Contain("Nonce");
    }

    [Fact]
    public void Validate_OversizedSignature_Rejected()
    {
        var env = new MessageEnvelope
        {
            ConversationId = Guid.NewGuid(),
            MessageId = Guid.NewGuid(),
            Seq = 1,
            EpochId = 1,
            Ciphertext = new byte[] { 1 },
            Signature = new byte[EnvelopeValidator.MaxSignatureBytes + 1]
        };
        _validator.Validate(env).Should().Contain("Signature");
    }

    [Fact]
    public void Validate_OversizedLeaseToken_Rejected()
    {
        var env = new MessageEnvelope
        {
            ConversationId = Guid.NewGuid(),
            MessageId = Guid.NewGuid(),
            Seq = 1,
            EpochId = 1,
            Ciphertext = new byte[] { 1 },
            LeaseToken = new string('x', EnvelopeValidator.MaxLeaseTokenChars + 1)
        };
        _validator.Validate(env).Should().Contain("LeaseToken");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Validate_NonPositiveSeq_Rejected(long seq)
    {
        var env = new MessageEnvelope
        {
            ConversationId = Guid.NewGuid(),
            MessageId = Guid.NewGuid(),
            Seq = seq,
            EpochId = 1,
            Ciphertext = new byte[] { 1 }
        };
        _validator.Validate(env).Should().Contain("Seq must be positive");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Validate_NonPositiveEpoch_Rejected(int epoch)
    {
        var env = new MessageEnvelope
        {
            ConversationId = Guid.NewGuid(),
            MessageId = Guid.NewGuid(),
            Seq = 1,
            EpochId = epoch,
            Ciphertext = new byte[] { 1 }
        };
        _validator.Validate(env).Should().Contain("EpochId must be positive");
    }

    [Fact]
    public void Validate_AllFieldsAtMax_ReturnsNull()
    {
        var env = new MessageEnvelope
        {
            ConversationId = Guid.NewGuid(),
            MessageId = Guid.NewGuid(),
            Seq = 1,
            EpochId = 1,
            SenderDeviceId = Guid.NewGuid(),
            Nonce = new byte[EnvelopeValidator.MaxNonceBytes],
            Ciphertext = new byte[EnvelopeValidator.MaxCiphertextBytes],
            Signature = new byte[EnvelopeValidator.MaxSignatureBytes],
            LeaseToken = new string('x', EnvelopeValidator.MaxLeaseTokenChars),
            Type = MessageType.Text
        };
        _validator.Validate(env).Should().BeNull();
    }
}
