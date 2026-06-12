using System.Text;
using ECHAT.Models.Domain;

namespace ECHAT.Client.Core.Services;

/// <summary>
/// Costruisce il prompt per la risposta automatica AI partendo dai messaggi recenti e dai nomi
/// dei membri. Anche la pulizia del reply (rimozione di prefissi tipo "You:" o "MyName:") sta qui.
/// </summary>
public static class AiReplyComposer
{
    public const int MaxContextMessages = 15;

    public static string BuildContext(
        IEnumerable<DecryptedMessage> messages,
        Guid myUserId,
        IReadOnlyDictionary<Guid, string> memberNames)
    {
        var myName = NameOf(myUserId, memberNames);
        var sb = new StringBuilder();
        sb.AppendLine($"You are {myName} in a group chat. Write ONLY the message text, no prefix like \"{myName}:\" or \"You:\".");
        sb.AppendLine("Reply naturally and briefly (1-2 sentences). Match the language others are using.");
        sb.AppendLine("Try to respond to the topic of the conversation inventing things if necessary, if the conversation is stale change the topic.");
        sb.AppendLine();
        sb.AppendLine("Recent messages:");

        foreach (var m in messages.Where(m => !m.Invisible).TakeLast(MaxContextMessages))
        {
            var name = NameOf(m.SenderDeviceId, memberNames);
            sb.AppendLine($"[{name}]: {m.Payload.Text}");
        }

        sb.AppendLine();
        sb.AppendLine($"Write your reply as {myName} (just the message, no name prefix):");
        return sb.ToString();
    }

    /// <summary>Rimuove prefissi come "You: " o "MyName: " che a volte il modello inserisce.</summary>
    public static string CleanReply(string reply, Guid myUserId, IReadOnlyDictionary<Guid, string> memberNames)
    {
        var myName = NameOf(myUserId, memberNames);
        if (reply.StartsWith("You: ", StringComparison.OrdinalIgnoreCase))
            return reply.Substring(5).Trim();
        if (reply.StartsWith($"{myName}: ", StringComparison.OrdinalIgnoreCase))
            return reply.Substring(myName.Length + 2).Trim();
        return reply.Trim();
    }

    private static string NameOf(Guid userId, IReadOnlyDictionary<Guid, string> names)
        => names.TryGetValue(userId, out var n) ? n : "Someone";
}
