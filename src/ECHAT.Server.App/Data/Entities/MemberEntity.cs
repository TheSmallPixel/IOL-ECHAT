namespace ECHAT.Server.App.Data.Entities;

public class MemberEntity
{
    public long Id { get; set; }
    public Guid ConversationId { get; set; }
    public Guid UserId { get; set; }
    public string Role { get; set; } = "Member";
    public DateTime JoinedAt { get; set; }
    public DateTime? RemovedAt { get; set; }
}
