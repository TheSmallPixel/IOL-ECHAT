namespace ECHAT.Server.Core.Interfaces;

public interface IMembershipReader
{
    /// <summary>
    /// Restituisce il ruolo dell'utente nella conversazione, o null se non è un membro attivo.
    /// Ruoli: "Owner", "Admin", "Member".
    /// </summary>
    Task<string?> GetRoleAsync(Guid userId, Guid conversationId);
}
