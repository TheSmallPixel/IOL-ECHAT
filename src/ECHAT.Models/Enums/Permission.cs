namespace ECHAT.Models.Enums;

public enum Permission
{
    Read,
    Write,
    Upload,
    Download,
    AddMember,
    RemoveMember,
    TransferOwnership,
    ManageRoles,
    Admin,
    DeleteConversation,
    /// <summary>Nascondere/mostrare messaggi altrui (moderazione). Owner/Admin/Moderator.</summary>
    ModerateMessages
}
