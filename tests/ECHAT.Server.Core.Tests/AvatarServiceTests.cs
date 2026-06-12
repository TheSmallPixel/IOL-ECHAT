using System.Net;
using System.Net.Http.Headers;
using ECHAT.Server.Core.Services;
using FluentAssertions;

namespace ECHAT.Server.Core.Tests;

public class AvatarServiceTests
{
    private const string AllowedUrl = "https://lh3.googleusercontent.com/a/pic.png";

    private readonly AvatarService _sut = new();

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;
        public int CallCount { get; private set; }

        public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) => _responder = responder;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CallCount++;
            return Task.FromResult(_responder(request));
        }
    }

    private static HttpResponseMessage ImageResponse(byte[] bytes, string? contentType = "image/png")
    {
        var resp = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
        if (contentType != null)
            resp.Content.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        return resp;
    }

    [Fact]
    public async Task FetchFromExternalUrlAsync_NullOrEmptyUrl_ReturnsNull()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        using var client = new HttpClient(handler);

        (await _sut.FetchFromExternalUrlAsync(client, null)).Should().BeNull();
        (await _sut.FetchFromExternalUrlAsync(client, "")).Should().BeNull();
        handler.CallCount.Should().Be(0); // no external call when there's no url
    }

    [Fact]
    public async Task FetchFromExternalUrlAsync_AllowedGoogleHost_ReturnsDataWithContentType()
    {
        var bytes = new byte[] { 1, 2, 3, 4 };
        var handler = new StubHandler(_ => ImageResponse(bytes, "image/png"));
        using var client = new HttpClient(handler);

        var result = await _sut.FetchFromExternalUrlAsync(client, AllowedUrl);

        result.Should().NotBeNull();
        result!.Data.Should().Equal(bytes);
        result.ContentType.Should().Be("image/png");
        handler.CallCount.Should().Be(1);
    }

    [Fact]
    public async Task FetchFromExternalUrlAsync_NoContentType_DefaultsToJpeg()
    {
        using var client = new HttpClient(new StubHandler(_ => ImageResponse(new byte[] { 9 }, contentType: null)));

        var result = await _sut.FetchFromExternalUrlAsync(client, AllowedUrl);

        result.Should().NotBeNull();
        result!.ContentType.Should().Be("image/jpeg");
    }

    [Fact]
    public async Task FetchFromExternalUrlAsync_NonSuccess_ReturnsNull()
    {
        using var client = new HttpClient(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound)));

        var result = await _sut.FetchFromExternalUrlAsync(client, AllowedUrl);

        result.Should().BeNull();
    }

    [Fact]
    public async Task FetchFromExternalUrlAsync_HttpException_ReturnsNull()
    {
        using var client = new HttpClient(new StubHandler(_ => throw new HttpRequestException("boom")));

        var result = await _sut.FetchFromExternalUrlAsync(client, AllowedUrl);

        result.Should().BeNull();
    }

    [Fact]
    public async Task FetchFromExternalUrlAsync_RejectsHttp_NoCallMade()
    {
        var handler = new StubHandler(_ => ImageResponse(new byte[] { 1 }));
        using var client = new HttpClient(handler);

        var result = await _sut.FetchFromExternalUrlAsync(client, "http://lh3.googleusercontent.com/a/pic.png");

        result.Should().BeNull();
        handler.CallCount.Should().Be(0); // SSRF guard rejects before any network call
    }

    [Fact]
    public async Task FetchFromExternalUrlAsync_RejectsNonAllowlistedHost_NoCallMade()
    {
        var handler = new StubHandler(_ => ImageResponse(new byte[] { 1 }));
        using var client = new HttpClient(handler);

        var result = await _sut.FetchFromExternalUrlAsync(client, "https://evil.example.com/pic.png");

        result.Should().BeNull();
        handler.CallCount.Should().Be(0);
    }

    [Fact]
    public async Task FetchFromExternalUrlAsync_RejectsPrivateIpHost_NoCallMade()
    {
        var handler = new StubHandler(_ => ImageResponse(new byte[] { 1 }));
        using var client = new HttpClient(handler);

        // Literal private IP host: SSRF target (e.g. cloud metadata / internal service).
        var result = await _sut.FetchFromExternalUrlAsync(client, "https://169.254.169.254/latest/meta-data");

        result.Should().BeNull();
        handler.CallCount.Should().Be(0);
    }

    [Fact]
    public async Task FetchFromExternalUrlAsync_OversizedResponse_ReturnsNull()
    {
        var big = new byte[AvatarService.MaxResponseBytes + 1];
        using var client = new HttpClient(new StubHandler(_ => ImageResponse(big)));

        var result = await _sut.FetchFromExternalUrlAsync(client, AllowedUrl);

        result.Should().BeNull();
    }

    // ---- Pure URL-validation logic ----

    [Theory]
    [InlineData("https://lh3.googleusercontent.com/a/pic.png", true)]
    [InlineData("https://googleusercontent.com/x", true)]
    [InlineData("https://www.google.com/x", true)]
    [InlineData("http://lh3.googleusercontent.com/a/pic.png", false)] // not https
    [InlineData("https://evil.example.com/x", false)]                 // not allow-listed
    [InlineData("https://googleusercontent.com.evil.com/x", false)]   // suffix trick
    [InlineData("https://10.0.0.5/x", false)]                         // private IP literal
    [InlineData("https://127.0.0.1/x", false)]                        // loopback IP literal
    [InlineData("https://192.168.1.10/x", false)]                     // private IP literal
    [InlineData("https://[::1]/x", false)]                            // IPv6 loopback literal
    [InlineData("ftp://lh3.googleusercontent.com/x", false)]          // wrong scheme
    [InlineData("not a url", false)]
    [InlineData(null, false)]
    public void IsAllowedUrl_AppliesSchemeHostAndIpRules(string? url, bool expected)
    {
        AvatarService.IsAllowedUrl(url, out var uri).Should().Be(expected);
        if (expected)
            uri.Should().NotBeNull();
        else
            uri.Should().BeNull();
    }

    [Theory]
    [InlineData("10.0.0.1", true)]
    [InlineData("172.16.5.4", true)]
    [InlineData("172.31.255.255", true)]
    [InlineData("172.32.0.1", false)] // outside 172.16/12
    [InlineData("192.168.0.1", true)]
    [InlineData("127.0.0.1", true)]
    [InlineData("169.254.0.1", true)]
    [InlineData("100.64.0.1", true)]
    [InlineData("8.8.8.8", false)]
    [InlineData("::1", true)]
    [InlineData("fc00::1", true)]
    [InlineData("fe80::1", true)]
    [InlineData("2001:4860:4860::8888", false)]
    public void IsPrivateOrReserved_ClassifiesAddresses(string ip, bool expected)
    {
        AvatarService.IsPrivateOrReserved(IPAddress.Parse(ip)).Should().Be(expected);
    }
}
