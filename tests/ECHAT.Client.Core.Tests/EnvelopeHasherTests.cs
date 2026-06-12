using ECHAT.Models.Domain;
using ECHAT.Models.Enums;
using FluentAssertions;

namespace ECHAT.Client.Core.Tests;

public class EnvelopeHasherTests
{
    private static MessageEnvelope Sample(
        long seq = 1,
        Guid? messageId = null,
        Guid? senderDeviceId = null,
        byte[]? ciphertext = null,
        byte[]? nonce = null) => new()
    {
        ConversationId = new Guid("11111111-1111-1111-1111-111111111111"),
        MessageId = messageId ?? new Guid("22222222-2222-2222-2222-222222222222"),
        Seq = seq,
        EpochId = 1,
        SenderDeviceId = senderDeviceId ?? new Guid("33333333-3333-3333-3333-333333333333"),
        Nonce = nonce ?? new byte[] { 7, 7, 7 },
        Ciphertext = ciphertext ?? new byte[] { 1, 2, 3, 4, 5 },
        Type = MessageType.Text
    };

    [Fact]
    public void Compute_IsDeterministic()
    {
        var env = Sample();
        EnvelopeHasher.Compute(env).Should().BeEquivalentTo(EnvelopeHasher.Compute(env));
    }

    [Fact]
    public void Compute_ProducesSha256_32Bytes()
    {
        EnvelopeHasher.Compute(Sample()).Should().HaveCount(32);
    }

    [Fact]
    public void Compute_DiffersForDifferentSeq()
    {
        EnvelopeHasher.Compute(Sample(seq: 1))
            .Should().NotBeEquivalentTo(EnvelopeHasher.Compute(Sample(seq: 2)));
    }

    [Fact]
    public void Compute_DiffersForDifferentCiphertext()
    {
        EnvelopeHasher.Compute(Sample(ciphertext: new byte[] { 1, 2, 3 }))
            .Should().NotBeEquivalentTo(EnvelopeHasher.Compute(Sample(ciphertext: new byte[] { 9, 9, 9 })));
    }

    [Fact]
    public void Compute_DiffersForDifferentSender()
    {
        EnvelopeHasher.Compute(Sample(senderDeviceId: Guid.NewGuid()))
            .Should().NotBeEquivalentTo(EnvelopeHasher.Compute(Sample(senderDeviceId: Guid.NewGuid())));
    }

    [Fact]
    public void Compute_DiffersForDifferentNonce()
    {
        // La firma copre il Nonce: cambiarlo deve cambiare il digest (altrimenti un nonce diverso
        // condividerebbe la stessa firma). Regressione che ha mascherato il bug del custode.
        EnvelopeHasher.Compute(Sample(nonce: new byte[] { 1, 1, 1 }))
            .Should().NotBeEquivalentTo(EnvelopeHasher.Compute(Sample(nonce: new byte[] { 2, 2, 2 })));
    }

    [Fact]
    public void Compute_DiffersForDifferentMessageId()
    {
        EnvelopeHasher.Compute(Sample(messageId: Guid.NewGuid()))
            .Should().NotBeEquivalentTo(EnvelopeHasher.Compute(Sample(messageId: Guid.NewGuid())));
    }
}
