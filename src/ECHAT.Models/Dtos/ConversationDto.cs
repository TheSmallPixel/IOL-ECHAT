namespace ECHAT.Models.Dtos;

public class ConversationDto
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public int CurrentEpochId { get; set; }
    public DateTime CreatedAt { get; set; }
    public string? MyRole { get; set; }
}
