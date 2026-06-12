namespace ECHAT.Server.App.Data.Entities;

public class AuditLogEntity
{
    public long Id { get; set; }
    public Guid ConversationId { get; set; }
    public Guid? UserId { get; set; }
    public string Action { get; set; } = string.Empty;
    public DateTime Timestamp { get; set; }
    public string? Details { get; set; }
}
