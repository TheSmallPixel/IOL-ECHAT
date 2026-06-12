using ECHAT.Client.Core.Services;
using ECHAT.Client.Core.Tests.Fakes;
using ECHAT.Models.Domain;
using ECHAT.Models.Enums;
using FluentAssertions;

namespace ECHAT.Client.Core.Tests;

public class CustodianWorkerTests
{
    private readonly Guid _conv = Guid.NewGuid();
    private readonly FakeChatServerGateway _gateway = new();
    private readonly FakeCryptoEngine _crypto = new();
    private readonly FakeDeviceKeyStore _keys = new();

    private CustodianWorker Sut() => new(
        _gateway,
        new InProcessCustodianCryptoOffloader(_crypto, new FileCipher(new FakeAead()), _keys),
        _keys);

    // Wrappa una CEK con la RSA del device (il custode la unwrappa). Sync ok: il fake è in-process.
    private byte[] Wrap(byte[] cek) => _keys.WrapCekAsync(cek, _keys.RsaSpki).GetAwaiter().GetResult();

    [Fact]
    public async Task ReencryptedEnvelope_SignatureVerifies_OverFullEnvelopeIncludingNonce()
    {
        // Regressione: se l'offloader firma un digest SENZA il Nonce ma l'envelope finale lo include,
        // la verifica fallisce. Questo test ricostruisce l'envelope finale (come CustodianWorker) e
        // verifica che la firma del custode regga sul digest completo (con Nonce).
        var offloader = new InProcessCustodianCryptoOffloader(_crypto, new FileCipher(new FakeAead()), _keys);
        var payload = new MessagePayload { Seq = 3, Text = "hello", PrevEnvelopeHash = System.Array.Empty<byte>() };
        var msgId = Guid.NewGuid();

        var enc = await offloader.EncryptAndSignAsync(
            payload, oldCek: new byte[] { 0xAA }, newCek: new byte[] { 0xBB },
            senderDeviceId: _keys.DeviceId, conversationId: _conv, messageId: msgId,
            seq: 3, newEpochId: 2, overridePrevEnvelopeHash: null, ct: System.Threading.CancellationToken.None);

        var finalEnvelope = new MessageEnvelope
        {
            ConversationId = _conv,
            MessageId = msgId,
            Seq = 3,
            EpochId = 2,
            SenderDeviceId = _keys.DeviceId,
            Nonce = enc.Nonce,
            Ciphertext = enc.Ciphertext,
            Signature = enc.Signature,
            Type = MessageType.Text
        };

        var digest = EnvelopeHasher.Compute(finalEnvelope);
        (await _keys.VerifySignatureAsync(digest, enc.Signature, _keys.EcdsaSpki))
            .Should().BeTrue("the custodian signature must verify over the full envelope incl. nonce");
    }

    [Fact]
    public async Task RewrapKeysForMember_DelegatesToGateway_WithIncludeHistory()
    {
        var newMember = Guid.NewGuid();

        await Sut().RewrapKeysForMemberAsync(_conv, newMember);

        _gateway.Adds.Should().ContainSingle()
            .Which.Should().Be((_conv, newMember, true));
    }

    [Fact]
    public async Task GenerateGapTombstones_BuildsCorrectRange()
    {
        await Sut().GenerateGapTombstonesAsync(_conv, fromSeq: 10, toSeq: 12);

        _gateway.Tombstones.Should().ContainSingle();
        var tombstones = _gateway.Tombstones[0].tombstones;
        tombstones.Should().HaveCount(3);
        tombstones.Select(t => t.Seq).Should().Equal(10, 11, 12);
        tombstones.Should().OnlyContain(t => t.EpochId == 1);
    }

    [Fact]
    public async Task GenerateGapTombstones_InvalidRange_Throws()
    {
        Func<Task> act = async () => await Sut().GenerateGapTombstonesAsync(_conv, fromSeq: 5, toSeq: 4);
        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task RunStrongRevoke_RewrapOnly_IsRejected_NoServerCalls()
    {
        // RewrapOnly non ha saga server-side: epoch bump + shred avvengono in RemoveMember.
        Func<Task> act = () => Sut().RunStrongRevokeAsync(_conv, MigrationMode.RewrapOnly, CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentException>();
        _gateway.Migrations.Should().BeEmpty();
        _gateway.Finalizes.Should().BeEmpty();
        _gateway.Replaced.Should().BeEmpty();
    }

    [Fact]
    public async Task RunStrongRevoke_FullReencrypt_DrivesRewriteLoop()
    {
        // Setup: epoch 1 ha la CEK vecchia, epoch 2 la nuova
        var oldCek = new byte[] { 0xAA };
        var newCek = new byte[] { 0xBB };
        _gateway.Keys[(_conv, 1)] = Wrap(oldCek);
        _gateway.Keys[(_conv, 2)] = Wrap(newCek);

        // 3 envelope sull'epoch 1 (da ricifrare) + 1 sull'epoch 2 (skippato)
        var sender = Guid.NewGuid();
        for (int seq = 1; seq <= 3; seq++)
        {
            var msgId = Guid.NewGuid();
            var payload = new MessagePayload { Seq = seq, Text = $"msg{seq}" };
            var enc = await _crypto.EncryptAsync(payload, oldCek, _conv, msgId, seq, epochId: 1);
            var sig = new byte[64]; // firma originale: irrilevante qui (il custode decifra e ri-firma)
            _gateway.Envelopes[_conv] = _gateway.Envelopes.GetValueOrDefault(_conv, new List<MessageEnvelope>());
            _gateway.Envelopes[_conv].Add(new MessageEnvelope
            {
                ConversationId = _conv, MessageId = msgId, Seq = seq, EpochId = 1,
                SenderDeviceId = sender, Ciphertext = enc.Ciphertext, Nonce = enc.Nonce,
                Signature = sig, LeaseToken = "x",
                Type = MessageType.Text
            });
        }
        var freshPayload = new MessagePayload { Seq = 4, Text = "gia-nuova" };
        var freshEnc = await _crypto.EncryptAsync(freshPayload, newCek, _conv, Guid.NewGuid(), 4, 2);
        _gateway.Envelopes[_conv].Add(new MessageEnvelope
        {
            ConversationId = _conv, MessageId = Guid.NewGuid(), Seq = 4, EpochId = 2,
            SenderDeviceId = sender, Ciphertext = freshEnc.Ciphertext, Nonce = freshEnc.Nonce,
            Signature = new byte[1], LeaseToken = "", Type = MessageType.Text
        });

        await Sut().RunStrongRevokeAsync(_conv, MigrationMode.FullReencrypt, CancellationToken.None);

        _gateway.Replaced.Should().HaveCount(3, "solo i 3 envelope dell'epoch vecchio vanno ricifrati");
        _gateway.Replaced.Should().OnlyContain(r => r.envelope.EpochId == 2);
        _gateway.Checkpoints.Should().NotBeEmpty();
        _gateway.Finalizes.Should().ContainSingle();
    }

    [Fact]
    public async Task RunStrongRevoke_FullReencrypt_NoKeys_Throws()
    {
        Func<Task> act = async () => await Sut().RunStrongRevokeAsync(
            _conv, MigrationMode.FullReencrypt, CancellationToken.None);
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*Could not load CEKs*");
    }

    [Fact]
    public async Task RunStrongRevoke_RespectsCancellation()
    {
        _gateway.Keys[(_conv, 1)] = Wrap(new byte[] { 1 });
        _gateway.Keys[(_conv, 2)] = Wrap(new byte[] { 2 });

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Func<Task> act = async () => await Sut().RunStrongRevokeAsync(
            _conv, MigrationMode.FullReencrypt, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }
}
