using ECHAT.Models.Domain;
using ECHAT.Models.Enums;

namespace ECHAT.Client.Core.Commands;

public class SendMessageCommand
{
    public Guid MessageId { get; init; }
    public Guid ConversationId { get; init; }
    public MessagePayload Payload { get; init; } = null!;
    public OutboxState State { get; set; }
}

public class OutboxItem
{
    public Guid MessageId { get; init; }
    public Guid ConversationId { get; init; }
    public MessagePayload Payload { get; init; } = null!;
    public OutboxState State { get; set; }
    public int RetryCount { get; set; }
    public string? FailureReason { get; set; }
}
