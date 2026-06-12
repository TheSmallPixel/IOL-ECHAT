using ECHAT.Models.Dtos;

namespace ECHAT.Server.Core.Interfaces;

/// <summary>
/// Ricerca utenti: validazione lunghezza minima, filtro (attivi, esclusione di se stessi, match
/// substring su Email/DisplayName), <c>Take(20)</c> e proiezione su <see cref="UserDto"/>.
/// Il controller mantiene route/[Authorize], estrazione claim e il logging.
/// </summary>
public interface IUserSearchService
{
    Task<List<UserDto>> SearchAsync(Guid currentUserId, string query);
}
