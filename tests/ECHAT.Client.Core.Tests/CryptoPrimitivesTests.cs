using ECHAT.Client.Core.Services;
using FluentAssertions;

namespace ECHAT.Client.Core.Tests;

// Cipher (AES-GCM) e signer (HMAC) hanno un'unica implementazione, WebCrypto, testata in JS
// (tests/js/crypto.test.mjs). Qui resta solo il gzip, che gira in C# anche in produzione.

public class GzipCompressorTests
{
    private readonly GzipCompressor _gz = new();

    [Fact]
    public void Compress_Decompress_RoundTrip()
    {
        var data = System.Text.Encoding.UTF8.GetBytes(new string('A', 4096));
        var compressed = _gz.Compress(data);
        var back = _gz.Decompress(compressed);
        back.Should().BeEquivalentTo(data);
    }

    [Theory]
    [InlineData("image/jpeg", 1024, false)]
    [InlineData("image/png", 1024, false)]
    [InlineData("video/mp4", 5000, false)]
    [InlineData("audio/mpeg", 5000, false)]
    [InlineData("application/zip", 1024, false)]
    [InlineData("application/pdf", 1024, false)]
    [InlineData("text/plain", 1024, true)]
    [InlineData("application/json", 1024, true)]
    [InlineData(null, 1024, true)]
    [InlineData("text/plain", 64, false)] // sotto soglia
    public void ShouldCompress_HonorsMimeAndSize(string? mime, long size, bool expected)
    {
        _gz.ShouldCompress(mime, size).Should().Be(expected);
    }
}
