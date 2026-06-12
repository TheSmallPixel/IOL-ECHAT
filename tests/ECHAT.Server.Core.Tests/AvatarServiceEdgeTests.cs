using System.Net;
using System.Net.Http.Headers;
using ECHAT.Server.Core.Services;
using FluentAssertions;

namespace ECHAT.Server.Core.Tests;

/// <summary>
/// Edge branches of <see cref="AvatarService"/> not exercised by AvatarServiceTests: the
/// declared Content-Length cap (rejected before the body is streamed) and the IPv4-mapped-IPv6
/// + multicast classification paths in <see cref="AvatarService.IsPrivateOrReserved"/>.
/// </summary>
public class AvatarServiceEdgeTests
{
    private const string AllowedUrl = "https://lh3.googleusercontent.com/a/pic.png";
    private readonly AvatarService _sut = new();

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        public int CallCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CallCount++;
            return Task.FromResult(responder(request));
        }
    }

    [Fact]
    public async Task FetchFromExternalUrlAsync_DeclaredContentLengthOverCap_ReturnsNull()
    {
        // The body is tiny, but the upstream lies with an oversized Content-Length header.
        // The cap must trip on the declared length, before the body is fully read.
        var handler = new StubHandler(_ =>
        {
            var resp = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(new byte[] { 1, 2, 3 })
            };
            resp.Content.Headers.ContentType = new MediaTypeHeaderValue("image/png");
            resp.Content.Headers.ContentLength = AvatarService.MaxResponseBytes + 1L;
            return resp;
        });
        using var client = new HttpClient(handler);

        var result = await _sut.FetchFromExternalUrlAsync(client, AllowedUrl);

        result.Should().BeNull();
        handler.CallCount.Should().Be(1); // request was made; rejected on declared length
    }

    [Fact]
    public async Task FetchFromExternalUrlAsync_DeclaredContentLengthWithinCap_Succeeds()
    {
        var bytes = new byte[] { 7, 7, 7 };
        var handler = new StubHandler(_ =>
        {
            var resp = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
            resp.Content.Headers.ContentType = new MediaTypeHeaderValue("image/png");
            resp.Content.Headers.ContentLength = bytes.Length; // honest, within cap
            return resp;
        });
        using var client = new HttpClient(handler);

        var result = await _sut.FetchFromExternalUrlAsync(client, AllowedUrl);

        result.Should().NotBeNull();
        result!.Data.Should().Equal(bytes);
    }

    [Theory]
    [InlineData("::ffff:10.0.0.1", true)]    // IPv4-mapped private -> classified via MapToIPv4
    [InlineData("::ffff:192.168.0.9", true)] // IPv4-mapped private
    [InlineData("::ffff:8.8.8.8", false)]    // IPv4-mapped public  -> allowed
    [InlineData("ff02::1", true)]            // IPv6 multicast
    public void IsPrivateOrReserved_HandlesIpv4MappedAndMulticast(string ip, bool expected)
    {
        AvatarService.IsPrivateOrReserved(IPAddress.Parse(ip)).Should().Be(expected);
    }
}
