using System.Net;
using System.Text;
using ECHAT.Server.Core.Interfaces;
using ECHAT.Server.Core.Services;
using FluentAssertions;

namespace ECHAT.Server.Core.Tests;

public class AiReplyServiceTests
{
    private readonly AiReplyService _sut = new();

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;
        public HttpRequestMessage? LastRequest { get; private set; }

        public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) => _responder = responder;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            return Task.FromResult(_responder(request));
        }
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string body) => new(status)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json")
    };

    [Fact]
    public async Task GetAiReplyAsync_MissingApiKey_Throws()
    {
        using var client = new HttpClient(new StubHandler(_ => Json(HttpStatusCode.OK, "{}")));
        var options = new AiReplyOptions { ApiKey = null, Model = "gpt-4o-mini" };

        var act = () => _sut.GetAiReplyAsync(client, options, "hello");

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("OpenAI API key not configured.");
    }

    [Fact]
    public async Task GetAiReplyAsync_ValidPrompt_ReturnsParsedReply()
    {
        var handler = new StubHandler(_ => Json(HttpStatusCode.OK,
            """{"choices":[{"message":{"role":"assistant","content":"  Hello there!  "}}]}"""));
        using var client = new HttpClient(handler);
        var options = new AiReplyOptions { ApiKey = "sk-test", Model = "gpt-4o-mini" };

        var reply = await _sut.GetAiReplyAsync(client, options, "hi");

        reply.Should().Be("Hello there!");
        handler.LastRequest!.RequestUri!.ToString().Should().Be("https://api.openai.com/v1/chat/completions");
        handler.LastRequest.Method.Should().Be(HttpMethod.Post);
        client.DefaultRequestHeaders.Contains("Authorization").Should().BeTrue();
    }

    [Fact]
    public async Task GetAiReplyAsync_UpstreamError_ThrowsWithStatusAndBody()
    {
        using var client = new HttpClient(new StubHandler(_ => Json(HttpStatusCode.TooManyRequests, "rate limited")));
        var options = new AiReplyOptions { ApiKey = "sk-test" };

        var ex = await Assert.ThrowsAsync<AiUpstreamException>(
            () => _sut.GetAiReplyAsync(client, options, "hi"));

        ex.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        ex.Body.Should().Be("rate limited");
    }

    [Fact]
    public async Task GetAiReplyAsync_EmptyChoices_ReturnsNull()
    {
        using var client = new HttpClient(new StubHandler(_ => Json(HttpStatusCode.OK, """{"choices":[]}""")));
        var options = new AiReplyOptions { ApiKey = "sk-test" };

        var reply = await _sut.GetAiReplyAsync(client, options, "hi");

        reply.Should().BeNull();
    }

    [Fact]
    public async Task GetAiReplyAsync_NoContentField_ReturnsNull()
    {
        using var client = new HttpClient(new StubHandler(_ => Json(HttpStatusCode.OK, """{"choices":[{"message":{"role":"assistant"}}]}""")));
        var options = new AiReplyOptions { ApiKey = "sk-test" };

        var reply = await _sut.GetAiReplyAsync(client, options, "hi");

        reply.Should().BeNull();
    }
}
