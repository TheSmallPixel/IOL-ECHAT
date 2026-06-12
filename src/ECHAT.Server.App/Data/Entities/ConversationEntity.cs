namespace ECHAT.Server.App.Data.Entities;

public class ConversationEntity
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public Guid CreatedByUserId { get; set; }
    public int CurrentEpochId { get; set; }
    public DateTime CreatedAt { get; set; }
}
