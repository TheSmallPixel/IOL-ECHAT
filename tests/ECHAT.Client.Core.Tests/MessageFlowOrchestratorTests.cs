using ECHAT.Client.Core.Services;
using ECHAT.Client.Core.Tests.Fakes;
using ECHAT.Models.Domain;
using ECHAT.Models.Dtos;
using ECHAT.Models.Enums;
using FluentAssertions;

namespace ECHAT.Client.Core.Tests;

public class MessageFlowOrchestratorTests
{
    private readonly Guid _conv = Guid.NewGuid();
    private readonly byte[] _cek = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();
    private readonly FakeChatServerGateway _gateway = new();
    private readonly FakeCryptoEngine _crypto = new();
    private readonly FakeDeviceKeyStore _keys = new();
    private readonly SequenceLeaseManager _leases = new();
    private readonly ChainValidator _chain;

    public MessageFlowOrchestratorTests()
    {
        _chain = new ChainValidator(_keys);
        // La CEK è ora wrappata con la chiave pubblica RSA del device: l'orchestratore la unwrappa.
        _gateway.Keys[(_conv, 1)] = Wrap(_cek);
        // Directory device: serve a verificare la firma dei messaggi ricevuti (deviceId  ECDSA SPKI).
        _gateway.Devices.Add(new DevicePublicKey
        {
            UserId = _gateway.CurrentUserId,
            DeviceId = _keys.DeviceId,
            RsaOaepSpki = _keys.RsaSpki,
            EcdsaSpki = _keys.EcdsaSpki
        });
    }

    // Wrappa una CEK come farebbe il server (RSA-OAEP verso il nostro device). Sync ok: il fake è in-process.
    private byte[] Wrap(byte[] cek) => _keys.WrapCekAsync(cek, _keys.RsaSpki).GetAwaiter().GetResult();

    private MessageFlowOrchestrator Sut() => new(_gateway, _crypto, _keys, _leases, _chain);

    [Fact]
    public async Task GetCurrentCek_FirstCall_FetchesFromGateway_AndCaches()
    {
        var sut = Sut();
        var (cek1, epoch1) = await sut.GetCurrentCekAsync(_conv);
        var (cek2, epoch2) = await sut.GetCurrentCekAsync(_conv);

        cek1.Should().BeEquivalentTo(_cek);
        epoch1.Should().Be(1);
        cek2.Should().BeEquivalentTo(_cek);
        epoch2.Should().Be(1);
    }

    [Fact]
    public async Task GetCurrentCek_NoKeyAvailable_Throws()
    {
        var sut = Sut();
        var unknownConv = Guid.NewGuid();

        Func<Task> act = async () => await sut.GetCurrentCekAsync(unknownConv);
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*No CEK*");
    }

    [Fact]
    public async Task GetCurrentCek_PicksHighestEpoch()
    {
        _gateway.Keys[(_conv, 2)] = Wrap(new byte[] { 0xFF });
        _gateway.Keys[(_conv, 3)] = Wrap(new byte[] { 0xEE });

        var (cek, epoch) = await Sut().GetCurrentCekAsync(_conv);

        epoch.Should().Be(3);
        cek.Should().BeEquivalentTo(new byte[] { 0xEE });
    }

    [Fact]
    public async Task GetCekForEpoch_FetchesSpecificEpoch_OnMiss()
    {
        _gateway.Keys[(_conv, 5)] = Wrap(new byte[] { 0xBB });

        var sut = Sut();
        var cek = await sut.GetCekForEpochAsync(_conv, 5);

        cek.Should().BeEquivalentTo(new byte[] { 0xBB });
    }

    [Fact]
    public async Task GetCekForEpoch_UnknownEpoch_ReturnsNull()
    {
        var cek = await Sut().GetCekForEpochAsync(_conv, epochId: 999);
        cek.Should().BeNull();
    }

    [Fact]
    public async Task SetCek_UpdatesCacheAndTracksCurrentEpoch()
    {
        var sut = Sut();
        sut.SetCek(_conv, 1, new byte[] { 1 });
        sut.SetCek(_conv, 3, new byte[] { 3 });
        sut.SetCek(_conv, 2, new byte[] { 2 }); // lower, non avanza current

        // Verifica indirettamente via GetCurrentCek (non rifà fetch perché la cache ha l'epoch più alto)
        var (cek, epoch) = await sut.GetCurrentCekAsync(_conv);
        epoch.Should().Be(3);
        cek.Should().BeEquivalentTo(new byte[] { 3 });
    }

    [Fact]
    public async Task SendMessage_BuildsEnvelope_ReservesSeq_OnFirstCall()
    {
        var sut = Sut();
        await sut.SendMessageAsync(_conv, "ciao", MessageFormat.Plain);

        _gateway.Sent.Should().ContainSingle();
        var env = _gateway.Sent[0];
        env.ConversationId.Should().Be(_conv);
        env.Seq.Should().Be(1);
        env.EpochId.Should().Be(1);
        env.Type.Should().Be(MessageType.Text);
        env.LeaseToken.Should().NotBeEmpty();
        env.Ciphertext.Should().NotBeEmpty();
        env.Signature.Should().NotBeEmpty();
        // Identità reale legata dal server al JWT (S4): device = questo device, user = utente corrente.
        env.SenderDeviceId.Should().Be(_keys.DeviceId);
        env.SenderUserId.Should().Be(_gateway.CurrentUserId);
        // Reservation per-send (count=1): vedi commento in MessageFlowOrchestrator. Le batch
        // reservation davano un'UX scadente con AI auto-reply (seq numerici fuori dall'ordine
        // cronologico).
        _gateway.LastSeqReservationCount.Should().Be(1);
    }

    [Fact]
    public async Task SendMessage_WithAttachments_TypeIsFileRef()
    {
        var sut = Sut();
        var attachment = new AttachmentRef { FileId = Guid.NewGuid(), FileName = "x.txt", Size = 10 };

        await sut.SendMessageAsync(_conv, "caption", MessageFormat.Plain, new List<AttachmentRef> { attachment });

        _gateway.Sent[0].Type.Should().Be(MessageType.FileRef);
    }

    [Fact]
    public async Task SendMessage_RequestsFreshSeq_OnEachCall()
    {
        var sut = Sut();
        await sut.SendMessageAsync(_conv, "uno", MessageFormat.Plain);
        _gateway.LastSeqReservationCount = 0; // reset

        await sut.SendMessageAsync(_conv, "due", MessageFormat.Plain);

        // Reservation per-send: ogni messaggio chiede al server il prossimo seq libero
        // proprio al momento di inviare, così i seq riflettono l'ordine reale di
        // reservation (importante con più mittenti attivi: AI auto-reply, multi-tab).
        _gateway.LastSeqReservationCount.Should().Be(1);
        _gateway.Sent[1].Seq.Should().Be(2);
    }

    [Fact]
    public async Task SendMessage_ChainsPreviousEnvelopeHash()
    {
        var sut = Sut();
        await sut.SendMessageAsync(_conv, "uno", MessageFormat.Plain);
        await sut.SendMessageAsync(_conv, "due", MessageFormat.Plain);

        // Il payload del secondo include il prev hash del primo
        var first = _gateway.Sent[0];
        var second = _gateway.Sent[1];
        var firstHash = _crypto.ComputeEnvelopeHash(first);

        var secondPayload = await _crypto.DecryptAsync(second.Ciphertext, second.Nonce, _cek,
            second.ConversationId, second.MessageId, second.Seq, second.EpochId);
        secondPayload.PrevEnvelopeHash.Should().BeEquivalentTo(firstHash);
    }

    [Fact]
    public async Task FetchMessages_DecryptsEachEnvelope_AndValidatesChain()
    {
        var sut = Sut();
        await sut.SendMessageAsync(_conv, "uno", MessageFormat.Plain);
        await sut.SendMessageAsync(_conv, "due", MessageFormat.Plain);

        var result = await sut.FetchMessagesAsync(_conv, afterSeq: null, beforeSeq: null, limit: 100);

        result.Should().HaveCount(2);
        result[0].Payload.Text.Should().Be("uno");
        result[1].Payload.Text.Should().Be("due");
        result.Should().OnlyContain(m => m.IsVerified);
    }

    [Fact]
    public async Task FetchMessages_DecryptFailure_ReturnsPlaceholderRow()
    {
        var sut = Sut();
        await sut.SendMessageAsync(_conv, "uno", MessageFormat.Plain);

        // Corrompi il ciphertext del messaggio salvato
        _gateway.Sent[0].Ciphertext[0] ^= 0xFF;
        _gateway.Envelopes[_conv][0] = _gateway.Sent[0];

        var result = await sut.FetchMessagesAsync(_conv, afterSeq: null, beforeSeq: null, limit: 100);

        result.Should().ContainSingle();
        var row = result[0];
        row.Payload.Text.Should().Be("[Decryption failed]");
        // Every validity flag on the failure row must be false (not just IsVerified): a mutant that
        // flips any single flag to true would otherwise survive.
        row.IsVerified.Should().BeFalse();
        row.SeqValid.Should().BeFalse();
        row.ChainValid.Should().BeFalse();
        row.DecryptionValid.Should().BeFalse();
        row.MacValid.Should().BeFalse();
        row.Invisible.Should().BeFalse();
    }

    [Fact]
    public async Task FetchMessages_SenderRemovedFromConversation_StillVerifiesViaHistoricalLookup()
    {
        // Un device di un membro RIMOSSO: non è più tra i device attivi della conversazione, ma resta
        // risolvibile via GetDeviceAsync (la rimozione del membro non revoca il device). I suoi messaggi
        // storici devono restare verificati.
        var removed = new FakeDeviceKeyStore();
        _gateway.DirectoryDevices.Add(new DevicePublicKey
        {
            UserId = Guid.NewGuid(),
            DeviceId = removed.DeviceId,
            RsaOaepSpki = removed.RsaSpki,
            EcdsaSpki = removed.EcdsaSpki
        });

        var msgId = Guid.NewGuid();
        var payload = new MessagePayload { Seq = 1, Text = "from removed member", PrevEnvelopeHash = Array.Empty<byte>() };
        var enc = await _crypto.EncryptAsync(payload, _cek, _conv, msgId, 1, 1);
        MessageEnvelope Build(byte[] sig) => new()
        {
            ConversationId = _conv,
            MessageId = msgId,
            Seq = 1,
            EpochId = 1,
            SenderDeviceId = removed.DeviceId,
            Nonce = enc.Nonce,
            Ciphertext = enc.Ciphertext,
            Signature = sig,
            Type = MessageType.Text
        };
        var sig = await removed.SignHashAsync(EnvelopeHasher.Compute(Build(Array.Empty<byte>())));
        _gateway.Envelopes[_conv] = new List<MessageEnvelope> { Build(sig) };

        var result = await Sut().FetchMessagesAsync(_conv, afterSeq: null, beforeSeq: null, limit: 100);

        result.Should().ContainSingle();
        result[0].Payload.Text.Should().Be("from removed member");
        result[0].IsVerified.Should().BeTrue("the removed member's device is still resolvable for signature verification");
    }

    [Fact]
    public async Task FetchMessages_UnknownEpoch_FallsToFailureBranch()
    {
        var sut = Sut();
        await sut.SendMessageAsync(_conv, "uno", MessageFormat.Plain);
        // Riscrivi l'envelope con un epoch sconosciuto (EpochId è init-only, costruiamo nuovo envelope)
        var orig = _gateway.Sent[0];
        var spoofed = new MessageEnvelope
        {
            ConversationId = orig.ConversationId,
            MessageId = orig.MessageId,
            Seq = orig.Seq,
            EpochId = 99,
            SenderDeviceId = orig.SenderDeviceId,
            Nonce = orig.Nonce,
            Ciphertext = orig.Ciphertext,
            Signature = orig.Signature,
            LeaseToken = orig.LeaseToken,
            Type = orig.Type
        };
        _gateway.Envelopes[_conv][0] = spoofed;

        var result = await sut.FetchMessagesAsync(_conv, afterSeq: null, beforeSeq: null, limit: 100);

        result.Should().ContainSingle();
        result[0].IsVerified.Should().BeFalse();
    }
}
