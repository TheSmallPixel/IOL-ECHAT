using System.Security.Cryptography;
using ECHAT.Client.Core.Interfaces;
using ECHAT.Client.Core.Services;
using ECHAT.Client.Core.Tests.Fakes;
using ECHAT.Models.Domain;
using ECHAT.Models.Enums;
using FluentAssertions;

namespace ECHAT.Client.Core.Tests;

public class FileEncryptionOrchestratorTests
{
    private readonly Guid _conv = Guid.NewGuid();
    private readonly Guid _user = Guid.NewGuid();
    private readonly byte[] _cek = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();

    private readonly FakeChatServerGateway _gateway = new();
    private readonly FakeCryptoEngine _crypto = new();
    private readonly FakeDeviceKeyStore _keys = new();
    private readonly SequenceLeaseManager _leases = new();
    private readonly ChainValidator _chain;

    private readonly FakeFileTransferManager _files = new();
    private readonly FileCipher _fileCipher = new(new FakeAead());
    private readonly FakeFileBodyCipher _body = new();
    private readonly FakeCustodianWorker _custodian = new();
    private readonly FakeMigrationStateTracker _migrations = new();

    private readonly MessageFlowOrchestrator _flow;

    public FileEncryptionOrchestratorTests()
    {
        _chain = new ChainValidator(_keys);
        // CEK wrappata con la RSA del device (l'orchestratore la unwrappa).
        _gateway.Keys[(_conv, 1)] = _keys.WrapCekAsync(_cek, _keys.RsaSpki).GetAwaiter().GetResult();
        _flow = new MessageFlowOrchestrator(_gateway, _crypto, _keys, _leases, _chain);
    }

    private FileEncryptionOrchestrator Sut()
        => new(_flow, _files, _fileCipher, _body, _custodian, _migrations, _gateway,
            new CekProvisioner(_gateway, _keys, _flow));

    // ---- SendEncryptedFileAsync ----

    [Fact]
    public async Task SendEncryptedFile_GeneratesDek_Uploads_AndSendsAttachment()
    {
        var sut = Sut();
        var fileBytes = new byte[] { 1, 2, 3, 4, 5 };
        List<AttachmentRef>? sentAttachments = null;
        string? sentText = null;

        await sut.SendEncryptedFileAsync(
            _conv, fileBytes, "doc.pdf", "application/pdf", "caption",
            (cid, text, atts) => { sentText = text; sentAttachments = atts; return Task.CompletedTask; });

        // Uploaded exactly one blob to the right conversation.
        _files.Uploads.Should().ContainSingle();
        _files.Uploads[0].conversationId.Should().Be(_conv);
        _files.Uploads[0].fileName.Should().Be("doc.pdf");

        // Caption + a single attachment with a wrapped DEK was sent.
        sentText.Should().Be("caption");
        sentAttachments.Should().ContainSingle();
        var att = sentAttachments![0];
        att.FileId.Should().Be(_files.NextFileId);
        att.FileName.Should().Be("doc.pdf");
        att.MimeType.Should().Be("application/pdf");
        att.Size.Should().Be(fileBytes.LongLength);
        att.WrappedFileDek.Should().NotBeEmpty();

        // The wrapped DEK actually unwraps under the CEK and matches the DEK embedded in the blob.
        var dek = await _fileCipher.UnwrapKeyAsync(att.WrappedFileDek, _cek);
        dek.Should().HaveCount(32);
        var blob = _files.Blobs[att.FileId];
        blob[0].Should().Be(0xA1);
        blob.Skip(1).Take(32).Should().Equal(dek);
    }

    [Fact]
    public async Task SendEncryptedFile_NullCaption_SendsEmptyString()
    {
        var sut = Sut();
        string? sentText = "unset";

        await sut.SendEncryptedFileAsync(
            _conv, new byte[] { 9 }, "f", "application/octet-stream", caption: null,
            (cid, text, atts) => { sentText = text; return Task.CompletedTask; });

        sentText.Should().Be(string.Empty);
    }

    // ---- DownloadAndDecryptAttachmentAsync ----

    [Fact]
    public async Task DownloadAndDecrypt_RoundTrips_CorrectCekByEpoch_AndDekUnwrap()
    {
        var sut = Sut();
        var plaintext = new byte[] { 10, 20, 30, 40 };

        // Encrypt + send to populate the blob store and obtain the attachment.
        AttachmentRef? att = null;
        await sut.SendEncryptedFileAsync(
            _conv, plaintext, "f", "application/octet-stream", null,
            (cid, text, atts) => { att = atts[0]; return Task.CompletedTask; });

        var result = await sut.DownloadAndDecryptAttachmentAsync(_conv, att!, epochId: 1);

        result.Should().Equal(plaintext);
    }

    [Fact]
    public async Task DownloadAndDecrypt_MissingMagicByte_ThrowsCryptographicException()
    {
        var sut = Sut();
        var fileId = Guid.NewGuid();
        _files.Blobs[fileId] = new byte[] { 0x00, 1, 2, 3 }; // wrong magic
        var att = new AttachmentRef { FileId = fileId, WrappedFileDek = new byte[48] };

        var act = () => sut.DownloadAndDecryptAttachmentAsync(_conv, att, epochId: 1);

        await act.Should().ThrowAsync<CryptographicException>().WithMessage("*0xA1*");
    }

    [Fact]
    public async Task DownloadAndDecrypt_EmptyBlob_ThrowsCryptographicException()
    {
        var sut = Sut();
        var fileId = Guid.NewGuid();
        _files.Blobs[fileId] = Array.Empty<byte>();
        var att = new AttachmentRef { FileId = fileId, WrappedFileDek = new byte[48] };

        var act = () => sut.DownloadAndDecryptAttachmentAsync(_conv, att, epochId: 1);

        await act.Should().ThrowAsync<CryptographicException>();
    }

    [Fact]
    public async Task DownloadAndDecrypt_NoCekForEpoch_ThrowsInvalidOperation()
    {
        var sut = Sut();
        var att = new AttachmentRef { FileId = Guid.NewGuid(), WrappedFileDek = new byte[48] };

        var act = () => sut.DownloadAndDecryptAttachmentAsync(_conv, att, epochId: 99);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*No CEK for epoch 99*");
    }

    // ---- HandleMemberRemovalAsync ----

    [Fact]
    public async Task HandleMemberRemoval_NullMigration_RemovesOnly()
    {
        var sut = Sut();

        await sut.HandleMemberRemovalAsync(_conv, _user, migration: null);

        _gateway.Removes.Should().ContainSingle().Which.Should().Be((_conv, _user));
        _gateway.Migrations.Should().BeEmpty();
        _custodian.StrongRevokes.Should().BeEmpty();
    }

    [Fact]
    public async Task HandleMemberRemoval_RewrapOnly_RemovesWithoutServerJob()
    {
        // RewrapOnly non avvia più alcun job server-side: l'epoch bump + shred dei wrap del
        // rimosso avvengono in RemoveMember, la nuova CEK è wrappata qui dal CekProvisioner.
        var sut = Sut();

        await sut.HandleMemberRemovalAsync(_conv, _user, MigrationMode.RewrapOnly);

        _gateway.Removes.Should().ContainSingle().Which.Should().Be((_conv, _user));
        _gateway.Migrations.Should().BeEmpty();
        _custodian.StrongRevokes.Should().BeEmpty();
    }

    [Fact]
    public async Task HandleMemberRemoval_FullReencrypt_BeginsLocal_AggregatesProgress_UpdatesTracker()
    {
        var sut = Sut();
        var reported = new List<MigrationProgress>();
        // Synchronous collector. The orchestrator's contract is "call IProgress.Report for each
        // phase, in order"; a sync collector verifies that deterministically. A Progress<T> here
        // would marshal via the thread pool and flake under parallel test load (reorder / late
        // delivery); that's a framework property, not something our code controls.
        var progress = new SyncCollector(reported);

        await sut.HandleMemberRemovalAsync(_conv, _user, MigrationMode.FullReencrypt, progress);

        _gateway.Removes.Should().ContainSingle();
        _custodian.StrongRevokes.Should().ContainSingle()
            .Which.Should().Be((_conv, MigrationMode.FullReencrypt));

        // BeginLocal scope opened and disposed.
        _migrations.BeginLocalCalls.Should().ContainSingle().Which.Should().Be(_conv);
        _migrations.EndLocalCount.Should().Be(1);

        // Tracker received every progress update emitted by the custodian.
        _migrations.Updates.Should().HaveCount(3);
        _migrations.Updates.Select(u => u.progress.Phase).Should()
            .Equal(MigrationPhase.Starting, MigrationPhase.Reencrypting, MigrationPhase.Completed);

        // The caller-supplied IProgress saw every phase, in order.
        reported.Select(p => p.Phase).Should()
            .Equal(MigrationPhase.Starting, MigrationPhase.Reencrypting, MigrationPhase.Completed);
    }

    [Fact]
    public async Task HandleMemberRemoval_UnknownMode_ThrowsArgumentOutOfRange()
    {
        var sut = Sut();
        var bogus = (MigrationMode)999;

        var act = () => sut.HandleMemberRemovalAsync(_conv, _user, bogus);

        await act.Should().ThrowAsync<ArgumentOutOfRangeException>();
        // Member is still removed before the switch throws (matches original behavior).
        _gateway.Removes.Should().ContainSingle();
    }

    /// <summary>
    /// <see cref="IProgress{T}"/> sincrono per i test: raccoglie i report inline, senza il
    /// marshaling async di <see cref="Progress{T}"/> (che sotto carico parallelo riordina/ritarda).
    /// </summary>
    private sealed class SyncCollector : IProgress<MigrationProgress>
    {
        private readonly List<MigrationProgress> _sink;
        public SyncCollector(List<MigrationProgress> sink) => _sink = sink;
        public void Report(MigrationProgress value) => _sink.Add(value);
    }
}
