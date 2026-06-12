using System.Security.Cryptography;
using ECHAT.Models.Domain;
using ECHAT.Server.Core.Exceptions;
using ECHAT.Server.Core.Interfaces;
using ECHAT.Server.Core.Pipeline;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace ECHAT.Server.Core.Tests.Pipeline;

public class SignatureVerificationHandlerTests
{
    private readonly Mock<IDevicePublicKeyStore> _devices = new();

    private SignatureVerificationHandler Sut()
        => new(_devices.Object, NullLogger<SignatureVerificationHandler>.Instance);

    private static MessageEnvelope BaseEnvelope(byte[] sig) => new()
    {
        ConversationId = Guid.NewGuid(),
        MessageId = Guid.NewGuid(),
        Seq = 7,
        EpochId = 1,
        SenderDeviceId = Guid.NewGuid(),
        Ciphertext = new byte[] { 1, 2, 3, 4 },
        Signature = sig,
    };

    // Firma come il client (WebCrypto): firma il digest di EnvelopeHasher con SHA-256 + P1363.
    private static byte[] Sign(ECDsa key, MessageEnvelope env)
        => key.SignData(EnvelopeHasher.Compute(env), HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation);

    [Fact]
    public async Task ValidSignature_CallsNext()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var spki = key.ExportSubjectPublicKeyInfo();
        var env = BaseEnvelope(Array.Empty<byte>());
        env = CloneWithSignature(env, Sign(key, env));

        _devices.Setup(d => d.GetActiveByDeviceAsync(env.SenderDeviceId))
            .ReturnsAsync(new DevicePublicKeyRecord(Guid.NewGuid(), env.SenderDeviceId, new byte[] { 9 }, spki, DateTime.UtcNow));

        var nextCalled = false;
        await Sut().HandleAsync(new IngestContext(env), () => { nextCalled = true; return Task.CompletedTask; });
        nextCalled.Should().BeTrue();
    }

    [Fact]
    public async Task TamperedSignature_ThrowsForbidden_AndDoesNotPersist()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var spki = key.ExportSubjectPublicKeyInfo();
        var env = BaseEnvelope(Array.Empty<byte>());
        var sig = Sign(key, env);
        sig[5] ^= 0xFF;
        env = CloneWithSignature(env, sig);

        _devices.Setup(d => d.GetActiveByDeviceAsync(env.SenderDeviceId))
            .ReturnsAsync(new DevicePublicKeyRecord(Guid.NewGuid(), env.SenderDeviceId, new byte[] { 9 }, spki, DateTime.UtcNow));

        var nextCalled = false;
        Func<Task> act = () => Sut().HandleAsync(new IngestContext(env), () => { nextCalled = true; return Task.CompletedTask; });
        await act.Should().ThrowAsync<ForbiddenException>();
        nextCalled.Should().BeFalse();
    }

    [Fact]
    public async Task SignatureFromADifferentKey_ThrowsForbidden()
    {
        using var signer = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var impostorDirectory = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var env = BaseEnvelope(Array.Empty<byte>());
        env = CloneWithSignature(env, Sign(signer, env));

        // La directory ha la chiave dell'impostore, non quella del firmatario.
        _devices.Setup(d => d.GetActiveByDeviceAsync(env.SenderDeviceId))
            .ReturnsAsync(new DevicePublicKeyRecord(Guid.NewGuid(), env.SenderDeviceId, new byte[] { 9 },
                impostorDirectory.ExportSubjectPublicKeyInfo(), DateTime.UtcNow));

        Func<Task> act = () => Sut().HandleAsync(new IngestContext(env), () => Task.CompletedTask);
        await act.Should().ThrowAsync<ForbiddenException>();
    }

    [Fact]
    public async Task UnknownDevice_ThrowsForbidden()
    {
        var env = BaseEnvelope(new byte[64]);
        _devices.Setup(d => d.GetActiveByDeviceAsync(It.IsAny<Guid>())).ReturnsAsync((DevicePublicKeyRecord?)null);

        Func<Task> act = () => Sut().HandleAsync(new IngestContext(env), () => Task.CompletedTask);
        await act.Should().ThrowAsync<ForbiddenException>();
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
        Type = e.Type,
    };
}
