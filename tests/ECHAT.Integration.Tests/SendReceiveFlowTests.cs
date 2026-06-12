using ECHAT.Client.Core.Services;
using ECHAT.Integration.Tests.Fakes;
using ECHAT.Models.Domain;
using ECHAT.Models.Dtos;
using ECHAT.Models.Enums;
using ECHAT.Server.Core.Interfaces;
using ECHAT.Server.Core.Pipeline;
using ECHAT.Server.Core.Services;
using FluentAssertions;
using Moq;

namespace ECHAT.Integration.Tests;

/// <summary>
/// Test del flusso end-to-end: Client.Core costruisce un messaggio, Server.Core lo ingerisce,
/// poi Client.Core lo recupera, decifra e valida la catena. Niente DB, browser o rete.
/// </summary>
public class SendReceiveFlowTests
{
    private readonly Guid _conversationId = Guid.NewGuid();
    private readonly RealDeviceKeyStore _keys = new();
    private Guid _deviceId => _keys.DeviceId;
    private readonly byte[] _cek = new byte[] { 0xAB, 0xCD, 0xEF };

    private readonly InMemoryMessageRepository _messageRepo = new();
    private readonly InMemorySeqCounterStore _counterStore = new();
    private readonly FakeCryptoEngine _crypto = new();
    private readonly Mock<IRealtimeNotifier> _notifier = new();
    private readonly Mock<ISequenceService> _seqService = new();
    private readonly Mock<IAuditLog> _audit = new();

    private MessageIngestPipeline BuildServerPipeline()
    {
        _seqService
            .Setup(s => s.ValidateLeaseAsync(It.IsAny<Guid>(), It.IsAny<long>(), It.IsAny<string>()))
            .ReturnsAsync(true);

        var handlers = new IIngestHandler[]
        {
            new LeaseValidationHandler(_seqService.Object),
            new DeduplicationHandler(_messageRepo),
            new PersistHandler(_messageRepo, _counterStore),
            new NotifyHandler(_notifier.Object),
            new AuditHandler(_audit.Object)
        };
        return new MessageIngestPipeline(handlers);
    }

    private async Task<MessageEnvelope> BuildEnvelope(long seq, string text, byte[] prevHash)
    {
        var messageId = Guid.NewGuid();
        var payload = new MessagePayload
        {
            Seq = seq,
            PrevEnvelopeHash = prevHash,
            Text = text
        };

        var encrypted = await _crypto.EncryptAsync(payload, _cek, _conversationId, messageId, seq, 1);

        // Firma ECDSA del device sul digest dell'envelope (come il client reale).
        MessageEnvelope Build(byte[] sig) => new()
        {
            ConversationId = _conversationId,
            MessageId = messageId,
            Seq = seq,
            EpochId = 1,
            SenderDeviceId = _deviceId,
            Nonce = encrypted.Nonce,
            Ciphertext = encrypted.Ciphertext,
            Signature = sig,
            LeaseToken = "valid",
            Type = MessageType.Text
        };
        var signature = await _keys.SignHashAsync(EnvelopeHasher.Compute(Build(Array.Empty<byte>())));
        return Build(signature);
    }

    [Fact]
    public async Task FullFlow_SendIngestFetchDecryptValidate_ShouldWork()
    {
        var pipeline = BuildServerPipeline();
        var chainValidator = new ChainValidator(_keys);

        // --- CLIENT: costruisce e invia 3 messaggi ---
        var lastHash = Array.Empty<byte>();
        var envelopes = new List<MessageEnvelope>();

        for (int i = 1; i <= 3; i++)
        {
            var envelope = await BuildEnvelope(i, $"Message {i}", lastHash);
            envelopes.Add(envelope);
            lastHash = _crypto.ComputeEnvelopeHash(envelope);

            // RETE: Client -> Server via POST /api/conversations/{id}/messages
            var ack = await pipeline.IngestAsync(envelope, envelope.SenderDeviceId);
            ack.Seq.Should().Be(i);
        }

        // RETE: Client -> Server via GET /api/conversations/{id}/messages?afterSeq=0&limit=100
        var stored = await _messageRepo.QueryAsync(_conversationId, null, null, 100);
        stored.Should().HaveCount(3);

        // CLIENT (locale): decifra e valida la catena
        var validatedHash = Array.Empty<byte>();
        var decryptedMessages = new List<DecryptedMessage>();

        foreach (var envelope in stored)
        {
            var payload = await _crypto.DecryptAsync(
                envelope.Ciphertext, envelope.Nonce, _cek,
                _conversationId, envelope.MessageId, envelope.Seq, envelope.EpochId);

            var chainResult = await chainValidator.ValidateAsync(envelope, payload, validatedHash, _keys.EcdsaSpki, decryptionSucceeded: true);
            chainResult.IsValid.Should().BeTrue($"chain should be valid at seq {envelope.Seq}");
            validatedHash = chainResult.CurrentEnvelopeHash;

            decryptedMessages.Add(new DecryptedMessage
            {
                MessageId = envelope.MessageId,
                Seq = envelope.Seq,
                Type = envelope.Type,
                Payload = payload,
                SenderDeviceId = envelope.SenderDeviceId,
                IsVerified = true,
                Invisible = payload.Invisible
            });
        }

        decryptedMessages.Should().HaveCount(3);
        decryptedMessages[0].Payload.Text.Should().Be("Message 1");
        decryptedMessages[1].Payload.Text.Should().Be("Message 2");
        decryptedMessages[2].Payload.Text.Should().Be("Message 3");
    }

    [Fact]
    public async Task FullFlow_DuplicateMessage_ShouldBeIdempotent()
    {
        var pipeline = BuildServerPipeline();
        var envelope = await BuildEnvelope(1, "Hello", Array.Empty<byte>());

        // RETE: Client -> Server via POST /api/conversations/{id}/messages
        var ack1 = await pipeline.IngestAsync(envelope, envelope.SenderDeviceId);
        // RETE: Client -> Server via POST /api/conversations/{id}/messages (duplicato)
        var ack2 = await pipeline.IngestAsync(envelope, envelope.SenderDeviceId);

        ack1.Seq.Should().Be(1);
        ack2.Seq.Should().Be(1);

        // RETE: Client -> Server via GET /api/conversations/{id}/messages
        var stored = await _messageRepo.QueryAsync(_conversationId, null, null, 100);
        stored.Should().HaveCount(1);
    }

    [Fact]
    public async Task FullFlow_ChainBreak_ShouldDetectTampering()
    {
        var pipeline = BuildServerPipeline();
        var chainValidator = new ChainValidator(_keys);

        // RETE: Client -> Server via POST /api/conversations/{id}/messages
        var msg1 = await BuildEnvelope(1, "First", Array.Empty<byte>());
        await pipeline.IngestAsync(msg1, msg1.SenderDeviceId);
        var hash1 = _crypto.ComputeEnvelopeHash(msg1);

        // RETE: Client -> Server via POST /api/conversations/{id}/messages
        var msg2 = await BuildEnvelope(2, "Tampered", new byte[] { 0xFF, 0xFF });
        await pipeline.IngestAsync(msg2, msg2.SenderDeviceId);

        // RETE: Client -> Server via GET /api/conversations/{id}/messages
        var stored = await _messageRepo.QueryAsync(_conversationId, null, null, 100);
        // CLIENT (locale): decifra e valida la catena
        var payload1 = await _crypto.DecryptAsync(stored[0].Ciphertext, stored[0].Nonce, _cek,
            _conversationId, stored[0].MessageId, stored[0].Seq, stored[0].EpochId);
        var result1 = await chainValidator.ValidateAsync(stored[0], payload1, Array.Empty<byte>(), _keys.EcdsaSpki, decryptionSucceeded: true);
        result1.IsValid.Should().BeTrue();

        var payload2 = await _crypto.DecryptAsync(stored[1].Ciphertext, stored[1].Nonce, _cek,
            _conversationId, stored[1].MessageId, stored[1].Seq, stored[1].EpochId);
        var result2 = await chainValidator.ValidateAsync(stored[1], payload2, result1.CurrentEnvelopeHash, _keys.EcdsaSpki, decryptionSucceeded: true);
        result2.IsValid.Should().BeFalse();
        result2.Error.Should().Be(ChainError.HashMismatch);
    }

    [Fact]
    public async Task FullFlow_OutboxToIngest_ShouldProcessPendingMessages()
    {
        var pipeline = BuildServerPipeline();
        var outbox = new OutboxService();

        // CLIENT (locale): mette il messaggio in outbox
        var payload = new MessagePayload { Seq = 1, PrevEnvelopeHash = Array.Empty<byte>(), Text = "From outbox" };
        var cmd = new Client.Core.Commands.SendMessageCommand
        {
            MessageId = Guid.NewGuid(),
            ConversationId = _conversationId,
            Payload = payload,
            State = OutboxState.Pending
        };
        await outbox.EnqueueAsync(cmd);

        // CLIENT (locale): processa l'outbox e costruisce l'envelope dal comando in attesa
        var pending = await outbox.GetPendingAsync();
        pending.Should().HaveCount(1);

        var encrypted = await _crypto.EncryptAsync(pending[0].Payload, _cek, _conversationId, pending[0].MessageId, 1, 1);
        var outboxSignature = new byte[64]; // questo flusso testa l'outboxingest, non verifica la firma
        var envelope = new MessageEnvelope
        {
            ConversationId = pending[0].ConversationId,
            MessageId = pending[0].MessageId,
            Seq = 1,
            EpochId = 1,
            SenderDeviceId = _deviceId,
            Nonce = encrypted.Nonce,
            Ciphertext = encrypted.Ciphertext,
            Signature = outboxSignature,
            LeaseToken = "valid",
            Type = MessageType.Text
        };

        // RETE: Client -> Server via POST /api/conversations/{id}/messages
        var ack = await pipeline.IngestAsync(envelope, envelope.SenderDeviceId);
        ack.Seq.Should().Be(1);

        // CLIENT (locale): segna come acked in outbox
        await outbox.AckAsync(pending[0].MessageId);

        var afterAck = await outbox.GetPendingAsync();
        afterAck.Should().BeEmpty();

        // RETE: Client -> Server via GET /api/conversations/{id}/messages
        var stored = await _messageRepo.QueryAsync(_conversationId, null, null, 100);
        stored.Should().HaveCount(1);

        // CLIENT (locale): decifra e verifica il contenuto
        var decrypted = await _crypto.DecryptAsync(stored[0].Ciphertext, stored[0].Nonce, _cek,
            _conversationId, stored[0].MessageId, stored[0].Seq, stored[0].EpochId);
        decrypted.Text.Should().Be("From outbox");
    }

    [Fact]
    public async Task FullFlow_CursorPagination_ShouldWorkCorrectly()
    {
        var pipeline = BuildServerPipeline();
        var lastHash = Array.Empty<byte>();

        // RETE: Client -> Server via POST /api/conversations/{id}/messages (x10)
        for (int i = 1; i <= 10; i++)
        {
            var envelope = await BuildEnvelope(i, $"Msg {i}", lastHash);
            lastHash = _crypto.ComputeEnvelopeHash(envelope);
            await pipeline.IngestAsync(envelope, envelope.SenderDeviceId);
        }

        // RETE: GET /messages?limit=3 senza cursore: ultimi 3 in ordine ascendente, così aprendo
        // la chat si vedono per primi i messaggi più recenti.
        var page1 = await _messageRepo.QueryAsync(_conversationId, null, null, 3);
        page1.Should().HaveCount(3);
        page1[0].Seq.Should().Be(8);
        page1[2].Seq.Should().Be(10);

        // RETE: GET /messages?afterSeq=3&limit=3: live-tail in avanti, slice ascendente.
        var page2 = await _messageRepo.QueryAsync(_conversationId, afterSeq: 3, null, 3);
        page2.Should().HaveCount(3);
        page2[0].Seq.Should().Be(4);
        page2[2].Seq.Should().Be(6);

        // RETE: GET /messages?beforeSeq=5&limit=100: tutti i messaggi con seq < 5, ascendenti.
        var before5 = await _messageRepo.QueryAsync(_conversationId, null, beforeSeq: 5, 100);
        before5.Should().HaveCount(4);
        before5.All(m => m.Seq < 5).Should().BeTrue();
        before5[0].Seq.Should().Be(1);

        // RETE: GET /messages?beforeSeq=8&limit=3: ultimi 3 strettamente più vecchi del seq 8 (ascendenti).
        // È la chiamata per lo scroll-up infinito.
        var beforePage = await _messageRepo.QueryAsync(_conversationId, null, beforeSeq: 8, 3);
        beforePage.Should().HaveCount(3);
        beforePage[0].Seq.Should().Be(5);
        beforePage[2].Seq.Should().Be(7);
    }

    [Fact]
    public async Task FullFlow_SequenceLeaseManager_ShouldProvideSeqForEncryption()
    {
        var pipeline = BuildServerPipeline();
        var leaseManager = new SequenceLeaseManager();

        // RETE: Client -> Server via POST /api/conversations/{id}/seq/reserve?count=3
        leaseManager.ApplyReservation(_conversationId, new SeqReservation
        {
            StartSeq = 100,
            EndSeq = 102,
            LeaseToken = "token",
            AnchorSeq = 99,
            AnchorEnvelopeHash = Array.Empty<byte>()
        });

        var lastHash = Array.Empty<byte>();

        for (int i = 0; i < 3; i++)
        {
            leaseManager.HasAvailableSeq(_conversationId).Should().BeTrue();
            var seq = await leaseManager.GetNextSeqAsync(_conversationId);
            var envelope = await BuildEnvelope(seq, $"Leased msg {seq}", lastHash);
            lastHash = _crypto.ComputeEnvelopeHash(envelope);

            // RETE: Client -> Server via POST /api/conversations/{id}/messages
            var ack = await pipeline.IngestAsync(envelope, envelope.SenderDeviceId);
            ack.Seq.Should().Be(100 + i);
        }

        leaseManager.HasAvailableSeq(_conversationId).Should().BeFalse();

        // RETE: Client -> Server via GET /api/conversations/{id}/messages
        var stored = await _messageRepo.QueryAsync(_conversationId, null, null, 100);
        stored.Should().HaveCount(3);
        stored.Select(m => m.Seq).Should().BeEquivalentTo(new long[] { 100, 101, 102 });
    }
}
