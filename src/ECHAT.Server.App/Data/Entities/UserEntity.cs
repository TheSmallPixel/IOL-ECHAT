namespace ECHAT.Server.App.Data.Entities;

public class UserEntity
{
    public Guid Id { get; set; }
    public string GoogleSubjectId { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string? PictureUrl { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime LastLoginAt { get; set; }
    public bool IsActive { get; set; } = true;
    public string PlatformRole { get; set; } = "User";
}
