using ECHAT.Client.Core.Interfaces;
using ECHAT.Client.Core.Services;
using ECHAT.Models.Domain;
using FluentAssertions;
using Moq;

namespace ECHAT.Client.Core.Tests;

public class AiAutoReplyOrchestratorTests
{
    private readonly Guid _me = Guid.NewGuid();
    private readonly Guid _alice = Guid.NewGuid();

    private readonly Mock<IAiReplyGenerator> _ai = new();
    private readonly Mock<IDelayProvider> _delay = new();

    private Dictionary<Guid, string> Names() => new() { [_me] = "Me", [_alice] = "Alice" };

    private AiAutoReplyOrchestrator Sut() => new(_ai.Object, _delay.Object);

    private static DecryptedMessage Msg(Guid sender, string text, bool invisible = false, long seq = 1)
        => new()
        {
            MessageId = Guid.NewGuid(),
            Seq = seq,
            SenderDeviceId = sender,
            Payload = new MessagePayload { Seq = seq, Text = text },
            Invisible = invisible
        };

    public AiAutoReplyOrchestratorTests()
    {
        // Delay no-op di default; i test che vogliono la cancellazione dopo il delay lo riconfigurano.
        _delay.Setup(d => d.DelayRandomAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
    }

    [Fact]
    public async Task RunAsync_HappyPath_Delays_Fetches_Generates_SendsCleanedReply()
    {
        var sut = Sut();
        var messages = new List<DecryptedMessage> { Msg(_alice, "ciao") };
        _ai.Setup(a => a.GenerateReplyAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("You: ehilà");   // prefisso "You: " che CleanReply rimuove

        string? sent = null;
        await sut.RunAsync(
            _me, Names(),
            ct => Task.FromResult(messages),
            (text, ct) => { sent = text; return Task.CompletedTask; },
            CancellationToken.None);

        sent.Should().Be("ehilà");
        _delay.Verify(d => d.DelayRandomAsync(
            AiAutoReplyOrchestrator.MinDelayMs, AiAutoReplyOrchestrator.MaxDelayMs, It.IsAny<CancellationToken>()),
            Times.Once);
        _ai.Verify(a => a.GenerateReplyAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task RunAsync_LastVisibleMessageIsMine_SkipsWithoutGeneratingOrSending()
    {
        var sut = Sut();
        // Ultimo visibile è mio (SenderDeviceId == myUserId): niente reply.
        var messages = new List<DecryptedMessage>
        {
            Msg(_alice, "ciao", seq: 1),
            Msg(_me, "rispondo io", seq: 2)
        };

        var sendCalled = false;
        await sut.RunAsync(
            _me, Names(),
            ct => Task.FromResult(messages),
            (text, ct) => { sendCalled = true; return Task.CompletedTask; },
            CancellationToken.None);

        sendCalled.Should().BeFalse();
        _ai.Verify(a => a.GenerateReplyAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task RunAsync_NoMessages_Skips()
    {
        var sut = Sut();
        var sendCalled = false;

        await sut.RunAsync(
            _me, Names(),
            ct => Task.FromResult(new List<DecryptedMessage>()),
            (text, ct) => { sendCalled = true; return Task.CompletedTask; },
            CancellationToken.None);

        sendCalled.Should().BeFalse();
        _ai.Verify(a => a.GenerateReplyAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task RunAsync_AllMessagesInvisible_Skips()
    {
        var sut = Sut();
        var messages = new List<DecryptedMessage>
        {
            Msg(_alice, "tombstone", invisible: true, seq: 1)
        };
        var sendCalled = false;

        await sut.RunAsync(
            _me, Names(),
            ct => Task.FromResult(messages),
            (text, ct) => { sendCalled = true; return Task.CompletedTask; },
            CancellationToken.None);

        sendCalled.Should().BeFalse();
        _ai.Verify(a => a.GenerateReplyAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task RunAsync_AiReturnsNullOrWhitespace_DoesNotSend(string? aiReply)
    {
        var sut = Sut();
        var messages = new List<DecryptedMessage> { Msg(_alice, "ciao") };
        _ai.Setup(a => a.GenerateReplyAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(aiReply);

        var sendCalled = false;
        await sut.RunAsync(
            _me, Names(),
            ct => Task.FromResult(messages),
            (text, ct) => { sendCalled = true; return Task.CompletedTask; },
            CancellationToken.None);

        sendCalled.Should().BeFalse();
    }

    [Fact]
    public async Task RunAsync_CleanedReplyBecomesEmpty_DoesNotSend()
    {
        var sut = Sut();
        var messages = new List<DecryptedMessage> { Msg(_alice, "ciao") };
        // "You: " è non-whitespace ma dopo CleanReply (strip + trim) diventa stringa vuota.
        _ai.Setup(a => a.GenerateReplyAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("You: ");

        var sendCalled = false;
        await sut.RunAsync(
            _me, Names(),
            ct => Task.FromResult(messages),
            (text, ct) => { sendCalled = true; return Task.CompletedTask; },
            CancellationToken.None);

        sendCalled.Should().BeFalse();
    }

    [Fact]
    public async Task RunAsync_CancelledAfterDelay_EarlyReturns_NoFetchNoSend()
    {
        var cts = new CancellationTokenSource();
        // Il delay "completa" cancellando il token: dopo il delay il flusso deve uscire subito.
        _delay.Setup(d => d.DelayRandomAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Returns(() => { cts.Cancel(); return Task.CompletedTask; });

        var sut = Sut();
        var fetchCalled = false;
        var sendCalled = false;

        await sut.RunAsync(
            _me, Names(),
            ct => { fetchCalled = true; return Task.FromResult(new List<DecryptedMessage>()); },
            (text, ct) => { sendCalled = true; return Task.CompletedTask; },
            cts.Token);

        fetchCalled.Should().BeFalse("the cancellation check after the delay should short-circuit before fetch");
        sendCalled.Should().BeFalse();
        _ai.Verify(a => a.GenerateReplyAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
