using ECHAT.Client.Core.Interfaces;
using ECHAT.Client.Core.Services;
using ECHAT.Integration.Tests.Fakes;
using ECHAT.Models.Domain;
using ECHAT.Models.Dtos;
using ECHAT.Models.Enums;
using ECHAT.Server.App.Data;
using ECHAT.Server.App.Data.Entities;
using ECHAT.Server.App.Repositories;
using ECHAT.Server.Core.Interfaces;
using ECHAT.Server.Core.Services;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Moq;

namespace ECHAT.Integration.Tests;

/// <summary>
/// End-to-end del flusso FullReencrypt: monta i service server-side veri (orchestrator,
/// stores, repository) e ci attacca un <see cref="CustodianWorker"/> client via
/// <see cref="InProcessChatServerGateway"/>. Verifica che la saga completi correttamente
/// quando il custode HA tutte le CEK, e che fallisca con <see cref="MigrationIncompleteException"/>
/// (e poi recuperi via force-finalize) quando NON le ha.
/// </summary>
public class FullReencryptIntegrationTests : IDisposable
{
    private readonly EchatDbContext _db;
    private readonly KeyEnvelopeRepository _keys;
    private readonly MigrationJobStore _jobs;
    private readonly ChainBoundaryStore _chainBoundaries;
    private readonly Mock<IRealtimeNotifier> _notifier = new();
    private readonly ICryptoEngine _crypto = new FakeCryptoEngine();
    private readonly FileCipher _fileCipher = new(new FakeAead());
    private readonly Guid _owner = Guid.NewGuid();
    // Key store reale condiviso: il seed wrappa le CEK con la sua RSA, il custode le unwrappa.
    private readonly FixedDeviceKeyStore _keyStore = new();

    public FullReencryptIntegrationTests()
    {
        var options = new DbContextOptionsBuilder<EchatDbContext>()
            .UseInMemoryDatabase($"echat-{Guid.NewGuid()}")
            .Options;
        _db = new EchatDbContext(options);
        _keys = new KeyEnvelopeRepository(_db);
        _jobs = new MigrationJobStore(_db);
        _chainBoundaries = new ChainBoundaryStore(_db);
    }

    public void Dispose() => _db.Dispose();

    private MigrationOrchestratorService Orchestrator() => new(
        _jobs,
        new MessageRepository(_db),
        new ConversationReader(_db),
        _keys,
        _notifier.Object,
        Microsoft.Extensions.Logging.Abstractions.NullLogger<MigrationOrchestratorService>.Instance,
        chainBoundaries: _chainBoundaries);

    private CustodianWorker BuildCustodian(Guid custodianUserId, MigrationOrchestratorService orchestrator)
    {
        var gateway = new InProcessChatServerGateway(
            custodianUserId, _db, _keys, orchestrator, _jobs, _chainBoundaries);
        var offloader = new InProcessCustodianCryptoOffloader(_crypto, _fileCipher, _keyStore);
        return new CustodianWorker(gateway, offloader, _keyStore);
    }

    /// <summary>
    /// Storico messaggi: crea una conversazione, distribuisce wraps a ognuno degli
    /// <paramref name="userWraps"/> per ognuno degli epoch indicati in
    /// <paramref name="epochsToCreate"/>, e produce messaggi cifrati con la CEK dell'epoch
    /// indicato nella tupla.
    /// </summary>
    private async Task<Guid> SeedConversation(
        int currentEpoch,
        Dictionary<int, Guid[]> userWrapsByEpoch,
        IEnumerable<(int epoch, int count)> messagesPerEpoch)
    {
        var conversationId = Guid.NewGuid();
        _db.Conversations.Add(new ConversationEntity
        {
            Id = conversationId,
            Name = "test-conv",
            CurrentEpochId = currentEpoch,
            CreatedAt = DateTime.UtcNow
        });

        // Genera una CEK distinta per ogni epoch + scrive le wraps per ogni utente che
        // dovrebbe avere accesso a quell'epoch (mappa userWrapsByEpoch).
        var ceks = new Dictionary<int, byte[]>();
        foreach (var (epoch, users) in userWrapsByEpoch)
        {
            var cek = new byte[32];
            Random.Shared.NextBytes(cek);
            ceks[epoch] = cek;

            var wraps = users.Select(uid => new WrappedKey
            {
                ConversationId = conversationId,
                EpochId = epoch,
                DeviceId = uid,
                // Wrappata con la RSA del key store condiviso, così il custode la unwrappa davvero.
                WrappedCek = _keyStore.WrapCekAsync(cek, _keyStore.RsaSpki).GetAwaiter().GetResult()
            }).ToList();
            await _keys.StoreWrapsAsync(conversationId, wraps);
        }

        long seq = 0;
        foreach (var (epoch, count) in messagesPerEpoch)
        {
            if (!ceks.TryGetValue(epoch, out var cek))
                throw new InvalidOperationException($"No CEK seeded for epoch {epoch}");

            for (int i = 0; i < count; i++)
            {
                seq++;
                var messageId = Guid.NewGuid();
                var payload = new MessagePayload
                {
                    Seq = seq,
                    Text = $"Test message at epoch {epoch} #{i}",
                    PrevEnvelopeHash = Array.Empty<byte>()
                };
                var encrypted = await _crypto.EncryptAsync(payload, cek, conversationId, messageId, seq, epoch);
                var signature = new byte[64]; // firma originale: il custode decifra (non verifica) e ri-firma
                _db.Messages.Add(new MessageEntity
                {
                    ConversationId = conversationId,
                    MessageId = messageId,
                    Seq = seq,
                    EpochId = epoch,
                    SenderDeviceId = _owner,
                    Nonce = encrypted.Nonce,
                    Ciphertext = encrypted.Ciphertext,
                    Signature = signature,
                    LeaseToken = "test-lease",
                    Type = MessageType.Text,
                    CreatedAt = DateTime.UtcNow
                });
            }
        }

        await _db.SaveChangesAsync();
        return conversationId;
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // Happy path: il custode (Owner) ha le CEK per tutti gli epoch storici. La saga
    // deve completare cleanly: tutti gli envelope ri-cifrati, ChainBoundary scritto,
    // wraps vecchi shred-dati, job marcato Completed.
    // ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Owner_WithAllWraps_CompletesFullReencrypt_ManyMessages()
    {
        // Owner ha wraps per epoch 1 (vecchio) e 2 (nuovo, post-rotation).
        // 200 messaggi tutti a epoch 1.
        var conv = await SeedConversation(
            currentEpoch: 2,
            userWrapsByEpoch: new()
            {
                [1] = new[] { _owner },
                [2] = new[] { _owner }
            },
            messagesPerEpoch: new[] { (1, 200) });

        var orchestrator = Orchestrator();
        var custodian = BuildCustodian(_owner, orchestrator);

        await custodian.RunStrongRevokeAsync(conv, MigrationMode.FullReencrypt, CancellationToken.None);

        // Job deve essere Completed
        var jobEntity = await _db.MigrationJobs.SingleAsync(j => j.ConversationId == conv);
        jobEntity.Status.Should().Be("Completed");
        jobEntity.MaxReplacedSeq.Should().Be(200);

        // Tutti gli envelope ora a epoch 2
        var atOld = await _db.Messages.CountAsync(m => m.ConversationId == conv && m.EpochId < 2);
        atOld.Should().Be(0, "all envelopes should have been rewritten");

        // Crypto-shred: wraps per epoch 1 cancellati
        var oldWraps = await _keys.GetKeysAsync(conv, epochId: 1, deviceId: null);
        oldWraps.Should().BeEmpty("epoch 1 wraps must be shredded after finalize");

        // ChainBoundary scritto
        var boundaries = await _chainBoundaries.ListAsync(conv);
        boundaries.Should().ContainSingle();
        boundaries[0].AfterSeq.Should().Be(200);
        boundaries[0].AtEpoch.Should().Be(2);
    }

    [Fact]
    public async Task Owner_WithMixedEpochs_RewritesOnlyOldOnes()
    {
        // Setup realistico: messaggi a epoch 1, 2 (vecchi), 3 (current, NON da riscrivere).
        var conv = await SeedConversation(
            currentEpoch: 3,
            userWrapsByEpoch: new()
            {
                [1] = new[] { _owner },
                [2] = new[] { _owner },
                [3] = new[] { _owner }
            },
            messagesPerEpoch: new[] { (1, 30), (2, 20), (3, 10) });

        var orchestrator = Orchestrator();
        var custodian = BuildCustodian(_owner, orchestrator);

        await custodian.RunStrongRevokeAsync(conv, MigrationMode.FullReencrypt, CancellationToken.None);

        // Tutti i 50 messaggi a epoch < 3 devono ora essere a epoch 3
        var atOld = await _db.Messages.CountAsync(m => m.ConversationId == conv && m.EpochId < 3);
        atOld.Should().Be(0);
        var atNew = await _db.Messages.CountAsync(m => m.ConversationId == conv && m.EpochId == 3);
        atNew.Should().Be(60);

        var jobEntity = await _db.MigrationJobs.SingleAsync(j => j.ConversationId == conv);
        jobEntity.Status.Should().Be("Completed");
        jobEntity.MaxReplacedSeq.Should().Be(50); // ultimo replace = seq 50 (l'ultimo a epoch 2)
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // Caso problematico: il custode NON ha le CEK per uno degli epoch storici.
    // Riproduce esattamente il sintomo dell'utente: N envelope restano al vecchio epoch,
    // saga deve fallire con MigrationIncompleteException PRIMA del finalize.
    // ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task AdminMissingOldEpochWraps_ThrowsMigrationIncompleteException()
    {
        var admin = Guid.NewGuid();

        // Owner ha wraps per epoch 1 + 2. Admin ha wraps solo per epoch 2.
        // 50 messaggi a epoch 1 (Admin non può decifrarli).
        var conv = await SeedConversation(
            currentEpoch: 2,
            userWrapsByEpoch: new()
            {
                [1] = new[] { _owner },         // solo l'Owner
                [2] = new[] { _owner, admin }   // entrambi
            },
            messagesPerEpoch: new[] { (1, 50) });

        var orchestrator = Orchestrator();
        var custodian = BuildCustodian(admin, orchestrator);

        var act = async () => await custodian.RunStrongRevokeAsync(
            conv, MigrationMode.FullReencrypt, CancellationToken.None);

        var ex = await act.Should().ThrowAsync<MigrationIncompleteException>();
        ex.Which.RemainingEnvelopes.Should().Be(50);

        // Il job deve essere stato cancellato automaticamente (cancel-on-failure)
        var jobEntity = await _db.MigrationJobs.SingleAsync(j => j.ConversationId == conv);
        jobEntity.Status.Should().Be("Cancelled", "saga failure should auto-cancel the orphan job");

        // I 50 envelope sono ancora a epoch 1 (intatti)
        var atOld = await _db.Messages.CountAsync(m => m.ConversationId == conv && m.EpochId == 1);
        atOld.Should().Be(50);

        // CEK per epoch 1 ANCORA presente (non shred-dato, perché Cancel non shreda)
        var oldWraps = await _keys.GetKeysAsync(conv, epochId: 1, deviceId: null);
        oldWraps.Should().NotBeEmpty("Cancel must NOT shred old keys; messages stay readable");
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // Recovery path: utente accetta esplicitamente la perdita via ForceFinalize.
    // ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ForceFinalize_OnStuckJob_BypassesSafetyCheck_AcceptsDataLoss()
    {
        var admin = Guid.NewGuid();
        var conv = await SeedConversation(
            currentEpoch: 2,
            userWrapsByEpoch: new()
            {
                [1] = new[] { _owner },
                [2] = new[] { _owner, admin }
            },
            messagesPerEpoch: new[] { (1, 30) });

        var orchestrator = Orchestrator();

        // 1) Admin avvia FullReencrypt: fallisce.
        var custodian = BuildCustodian(admin, orchestrator);
        var act = async () => await custodian.RunStrongRevokeAsync(
            conv, MigrationMode.FullReencrypt, CancellationToken.None);
        var ex = await act.Should().ThrowAsync<MigrationIncompleteException>();
        var stuckJobId = ex.Which.JobId;
        ex.Which.RemainingEnvelopes.Should().Be(30);

        // 2) Il job è stato auto-cancellato dal saga failure. ForceFinalize su un job
        // già terminale è idempotente (no-op): per testare ForceFinalize dobbiamo
        // ri-avviare e arrivare al punto di fallimento PRIMA del finalize.

        // Resettiamo: ri-startiamo una NUOVA saga e questa volta force-finalize prima del
        // pre-check fallisca.
        // Strategia: startiamo la saga (job InProgress), poi chiamiamo ForceFinalize
        // direttamente sull'orchestrator (bypassa il CustodianWorker che fallirebbe pre-check).
        var newJobId = await orchestrator.StartMigrationAsync(
            conv, MigrationMode.FullReencrypt, admin);

        await orchestrator.ForceFinalizeAsync(conv, newJobId);

        // Job deve essere Completed
        var jobEntity = await _db.MigrationJobs.SingleAsync(j => j.Id == newJobId);
        jobEntity.Status.Should().Be("Completed");

        // Crypto-shred È avvenuto nonostante envelope rimanenti
        var oldWraps = await _keys.GetKeysAsync(conv, epochId: 1, deviceId: null);
        oldWraps.Should().BeEmpty("ForceFinalize must shred old keys, accepting data loss");

        // I 30 envelope sono ancora a epoch 1 (con ciphertext intatto) ma ora illeggibili
        var stillThere = await _db.Messages.CountAsync(m => m.ConversationId == conv && m.EpochId == 1);
        stillThere.Should().Be(30, "envelopes are not deleted, only their decryption key is");
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // Server-side safety: anche se il CLIENT pre-check fosse aggirato, il server
    // deve comunque rifiutare il finalize.
    // ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ServerFinalize_WithoutAllRewritten_ThrowsConflict()
    {
        var conv = await SeedConversation(
            currentEpoch: 2,
            userWrapsByEpoch: new()
            {
                [1] = new[] { _owner },
                [2] = new[] { _owner }
            },
            messagesPerEpoch: new[] { (1, 10) });

        var orchestrator = Orchestrator();
        var jobId = await orchestrator.StartMigrationAsync(conv, MigrationMode.FullReencrypt, _owner);

        // Senza fare alcun replace, chiamiamo Finalize: il safety check del server deve
        // rifiutare perché 10 envelope sono ancora a epoch < 2.
        var act = async () => await orchestrator.FinalizeAsync(conv, jobId);
        await act.Should().ThrowAsync<ECHAT.Server.Core.Exceptions.ConflictException>()
            .WithMessage("*Cannot finalize: 10 envelope(s)*");

        // Le wraps vecchie restano (non shred)
        var oldWraps = await _keys.GetKeysAsync(conv, epochId: 1, deviceId: null);
        oldWraps.Should().NotBeEmpty();
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // Lots of messages: stress test per assicurarci che il loop a batch + chain rebuild
    // non saltino o duplichino niente.
    // ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Owner_WithVeryManyMessages_RewritesAllInBatches()
    {
        const int messageCount = 500; // > batchsize del custode (50)

        var conv = await SeedConversation(
            currentEpoch: 2,
            userWrapsByEpoch: new()
            {
                [1] = new[] { _owner },
                [2] = new[] { _owner }
            },
            messagesPerEpoch: new[] { (1, messageCount) });

        var orchestrator = Orchestrator();
        var custodian = BuildCustodian(_owner, orchestrator);

        await custodian.RunStrongRevokeAsync(conv, MigrationMode.FullReencrypt, CancellationToken.None);

        var atOld = await _db.Messages.CountAsync(m => m.ConversationId == conv && m.EpochId < 2);
        atOld.Should().Be(0, $"all {messageCount} envelopes should be at the new epoch");

        var atNew = await _db.Messages.CountAsync(m => m.ConversationId == conv && m.EpochId == 2);
        atNew.Should().Be(messageCount);

        var jobEntity = await _db.MigrationJobs.SingleAsync(j => j.ConversationId == conv);
        jobEntity.Status.Should().Be("Completed");
        jobEntity.MaxReplacedSeq.Should().Be(messageCount);
    }

    // Device key store con crypto .NET reale (ECDSA P-256 + RSA-OAEP-SHA256), stessi formati di
    // WebCrypto. Condiviso fra il seed (che wrappa le CEK) e il custode (che le unwrappa).
    private sealed class FixedDeviceKeyStore : IDeviceKeyStore
    {
        private const byte Magic = 0xB2;
        private readonly System.Security.Cryptography.ECDsa _ecdsa =
            System.Security.Cryptography.ECDsa.Create(System.Security.Cryptography.ECCurve.NamedCurves.nistP256);
        private readonly System.Security.Cryptography.RSA _rsa =
            System.Security.Cryptography.RSA.Create(2048);
        public Guid DeviceId { get; } = Guid.NewGuid();
        public byte[] RsaSpki => _rsa.ExportSubjectPublicKeyInfo();
        public byte[] EcdsaSpki => _ecdsa.ExportSubjectPublicKeyInfo();

        public Task<DeviceKeys> EnsureDeviceAsync() => Task.FromResult(new DeviceKeys(DeviceId, RsaSpki, EcdsaSpki));
        public Task<Guid> GetDeviceIdAsync() => Task.FromResult(DeviceId);
        public Task<byte[]> SignHashAsync(byte[] hash) => Task.FromResult(_ecdsa.SignData(hash,
            System.Security.Cryptography.HashAlgorithmName.SHA256,
            System.Security.Cryptography.DSASignatureFormat.IeeeP1363FixedFieldConcatenation));
        public Task<bool> VerifySignatureAsync(byte[] hash, byte[] sig, byte[] spki)
        {
            using var v = System.Security.Cryptography.ECDsa.Create();
            v.ImportSubjectPublicKeyInfo(spki, out _);
            return Task.FromResult(v.VerifyData(hash, sig,
                System.Security.Cryptography.HashAlgorithmName.SHA256,
                System.Security.Cryptography.DSASignatureFormat.IeeeP1363FixedFieldConcatenation));
        }
        public Task<byte[]> WrapCekAsync(byte[] cek, byte[] recipientRsaSpki)
        {
            using var r = System.Security.Cryptography.RSA.Create();
            r.ImportSubjectPublicKeyInfo(recipientRsaSpki, out _);
            var w = r.Encrypt(cek, System.Security.Cryptography.RSAEncryptionPadding.OaepSHA256);
            var blob = new byte[1 + w.Length]; blob[0] = Magic; w.CopyTo(blob, 1);
            return Task.FromResult(blob);
        }
        public Task<byte[]> UnwrapCekAsync(byte[] blob)
            => Task.FromResult(_rsa.Decrypt(blob[1..], System.Security.Cryptography.RSAEncryptionPadding.OaepSHA256));
    }
}
