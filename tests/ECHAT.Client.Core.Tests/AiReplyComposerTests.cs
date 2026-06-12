using ECHAT.Client.Core.Services;
using ECHAT.Models.Domain;
using FluentAssertions;

namespace ECHAT.Client.Core.Tests;

public class AiReplyComposerTests
{
    private readonly Guid _me = Guid.NewGuid();
    private readonly Guid _alice = Guid.NewGuid();
    private readonly Guid _bob = Guid.NewGuid();

    private Dictionary<Guid, string> Names() => new()
    {
        [_me] = "Me",
        [_alice] = "Alice",
        [_bob] = "Bob"
    };

    private static DecryptedMessage Msg(Guid sender, string text, bool invisible = false, long seq = 1)
        => new()
        {
            MessageId = Guid.NewGuid(),
            Seq = seq,
            SenderDeviceId = sender,
            Payload = new MessagePayload { Seq = seq, Text = text },
            Invisible = invisible
        };

    [Fact]
    public void BuildContext_IncludesRecentVisibleMessages_WithSenderNames()
    {
        var messages = new[]
        {
            Msg(_alice, "Ciao tutti", seq: 1),
            Msg(_bob, "Ehi", seq: 2),
            Msg(_me, "Hi", seq: 3)
        };

        var prompt = AiReplyComposer.BuildContext(messages, _me, Names());

        prompt.Should().Contain("[Alice]: Ciao tutti");
        prompt.Should().Contain("[Bob]: Ehi");
        prompt.Should().Contain("[Me]: Hi");
        prompt.Should().Contain("You are Me");
    }

    [Fact]
    public void BuildContext_SkipsInvisibleMessages()
    {
        var messages = new[]
        {
            Msg(_alice, "visibile", seq: 1),
            Msg(_bob, "tombstone", invisible: true, seq: 2)
        };

        var prompt = AiReplyComposer.BuildContext(messages, _me, Names());

        prompt.Should().Contain("[Alice]: visibile");
        prompt.Should().NotContain("tombstone");
    }

    [Fact]
    public void BuildContext_UnknownSenderIsLabeledSomeone()
    {
        var unknown = Guid.NewGuid();
        var messages = new[] { Msg(unknown, "boh", seq: 1) };

        var prompt = AiReplyComposer.BuildContext(messages, _me, Names());

        prompt.Should().Contain("[Someone]: boh");
    }

    [Fact]
    public void BuildContext_LimitsToMaxContextMessages()
    {
        var msgs = Enumerable.Range(1, AiReplyComposer.MaxContextMessages + 5)
            .Select(i => Msg(_alice, $"m{i}", seq: i))
            .ToList();

        var prompt = AiReplyComposer.BuildContext(msgs, _me, Names());

        // Devono comparire solo gli ultimi MaxContextMessages.
        prompt.Should().NotContain("m1\n");
        prompt.Should().Contain($"m{msgs.Count}");
    }

    [Theory]
    [InlineData("You: ciao", "ciao")]
    [InlineData("you: ciao", "ciao")]
    [InlineData("Me: come stai", "come stai")]
    [InlineData("normale", "normale")]
    public void CleanReply_StripsKnownPrefixes(string raw, string expected)
    {
        var cleaned = AiReplyComposer.CleanReply(raw, _me, Names());
        cleaned.Should().Be(expected);
    }

    [Fact]
    public void CleanReply_NoPrefix_Trimmed()
    {
        var cleaned = AiReplyComposer.CleanReply("  pulito  ", _me, Names());
        cleaned.Should().Be("pulito");
    }
}
