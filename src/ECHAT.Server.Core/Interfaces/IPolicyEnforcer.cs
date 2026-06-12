using ECHAT.Models.Enums;

namespace ECHAT.Server.Core.Interfaces;

public interface IPolicyEnforcer
{
    /// <summary>
    /// Decide se <paramref name="userId"/> può eseguire <paramref name="permission"/> nella
    /// conversazione, in base al suo ruolo di membership (Owner/Admin/Member). Unica fonte di
    /// verità per l'autorizzazione conversation-scoped: usata sia dal filtro MVC che dai service.
    /// </summary>
    Task<bool> AuthorizeAsync(Guid userId, Guid conversationId, Permission permission);

    /// <summary>
    /// Decide se <paramref name="userId"/> è un amministratore di piattaforma (ruolo globale,
    /// non legato a una conversazione). Usata per gli endpoint /api/admin.
    /// </summary>
    Task<bool> AuthorizePlatformAsync(Guid userId);
}
