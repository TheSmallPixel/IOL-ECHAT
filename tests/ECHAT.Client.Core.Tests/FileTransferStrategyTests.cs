using ECHAT.Client.Core.Services;
using FluentAssertions;

namespace ECHAT.Client.Core.Tests;

public class FileTransferStrategyTests
{
    private readonly FileTransferStrategy _strategy = new();

    [Fact]
    public void CalculatePartition_ZeroBytes_ZeroParts()
    {
        var p = _strategy.CalculatePartition(0);
        p.PartCount.Should().Be(0);
    }

    [Fact]
    public void CalculatePartition_FiveMiB_ThreeParts()
    {
        var p = _strategy.CalculatePartition(5 * 1024 * 1024);
        p.PartCount.Should().Be(3);
        p.PartSize.Should().Be(FileTransferStrategy.PartSize);
    }

    [Fact]
    public void CalculatePartition_ExactlyOnePart()
    {
        _strategy.CalculatePartition(FileTransferStrategy.PartSize).PartCount.Should().Be(1);
    }

    [Fact]
    public void CalculatePartition_OneByteOverBoundary_RoundsUp()
    {
        _strategy.CalculatePartition(FileTransferStrategy.PartSize + 1).PartCount.Should().Be(2);
    }

    [Fact]
    public async Task BufferStream_ExactMemoryStream_ReturnsUnderlyingBufferNoCopy()
    {
        var bytes = new byte[1024];
        new Random(1).NextBytes(bytes);
        // ctor with publiclyVisible=true exposes the exact buffer (length == count).
        var ms = new MemoryStream(bytes.Length);
        ms.Write(bytes, 0, bytes.Length);
        ms.Position = 0;

        var result = await _strategy.BufferStreamAsync(ms, CancellationToken.None);

        result.Should().Equal(bytes);
        // Underlying buffer was exact-sized and aligned -> returned without copy.
        ms.TryGetBuffer(out var seg).Should().BeTrue();
        ReferenceEquals(result, seg.Array).Should().BeTrue();
    }

    [Fact]
    public async Task BufferStream_NonSeekableStream_Copies()
    {
        var bytes = new byte[4096];
        new Random(2).NextBytes(bytes);
        using var inner = new MemoryStream(bytes);
        using var wrapper = new NonBufferStream(inner);

        var result = await _strategy.BufferStreamAsync(wrapper, CancellationToken.None);

        result.Should().Equal(bytes);
    }

    [Fact]
    public async Task OrchestrateBatchedUpload_FiveParts_TwoBatches()
    {
        // 5 parts, MaxConcurrent=4 -> batch sizes [4,1] -> 2 onProgress calls.
        var dataLen = FileTransferStrategy.PartSize * 4 + 100; // 5 parts
        var data = new byte[dataLen];
        var partition = _strategy.CalculatePartition(dataLen);
        partition.PartCount.Should().Be(5);

        var uploadedParts = new List<int>();
        var progressReports = new List<int>();

        await _strategy.OrchestrateBatchedUploadAsync(
            data,
            partition,
            (partNo, offset, len) => { lock (uploadedParts) uploadedParts.Add(partNo); return Task.CompletedTask; },
            completed => progressReports.Add(completed),
            CancellationToken.None);

        uploadedParts.Should().HaveCount(5);
        uploadedParts.Should().BeEquivalentTo(new[] { 0, 1, 2, 3, 4 });
        progressReports.Should().Equal(4, 5); // one report per batch, cumulative
    }

    [Fact]
    public async Task OrchestrateBatchedUpload_PassesCorrectOffsetsAndLengths()
    {
        var dataLen = FileTransferStrategy.PartSize + 500; // 2 parts: full + remainder
        var data = new byte[dataLen];
        var partition = _strategy.CalculatePartition(dataLen);

        var seen = new List<(int partNo, int offset, int len)>();
        await _strategy.OrchestrateBatchedUploadAsync(
            data,
            partition,
            (partNo, offset, len) => { lock (seen) seen.Add((partNo, offset, len)); return Task.CompletedTask; },
            _ => { },
            CancellationToken.None);

        seen.Should().Contain((0, 0, FileTransferStrategy.PartSize));
        seen.Should().Contain((1, FileTransferStrategy.PartSize, 500));
    }

    [Fact]
    public async Task OrchestrateBatchedUpload_Cancellation_Throws()
    {
        var dataLen = FileTransferStrategy.PartSize * 8; // 8 parts -> >1 batch
        var data = new byte[dataLen];
        var partition = _strategy.CalculatePartition(dataLen);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var act = () => _strategy.OrchestrateBatchedUploadAsync(
            data, partition,
            (_, _, _) => Task.CompletedTask,
            _ => { },
            cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    private sealed class NonBufferStream : Stream
    {
        private readonly Stream _inner;
        public NonBufferStream(Stream inner) => _inner = inner;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
