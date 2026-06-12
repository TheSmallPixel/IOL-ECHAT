using ECHAT.Server.Core.Interfaces;

namespace ECHAT.Server.Core.Services;

/// <summary>
/// Stato corrente dei campi mergeable di un utente esistente, passato dal repository EF.
/// Mantiene Core libero da <c>UserEntity</c> (che vive solo nella App).
/// </summary>
public class ExistingUserSnapshot
{
    public string DisplayName { get; init; } = string.Empty;
    public string? PictureUrl { get; init; }
    public DateTime CreatedAt { get; init; }
}

/// <summary>
/// Campi risultanti dall'upsert da applicare all'entity (o usare per creare la nuova riga).
/// </summary>
public class UserUpsertResult
{
    public bool IsNew { get; init; }
    public string Email { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public string? PictureUrl { get; init; }
    public DateTime CreatedAt { get; init; }
    public DateTime LastLoginAt { get; init; }
}

/// <summary>
/// Logica pura (senza EF) dell'albero di decisione dell'upsert Google e delle regole di merge:
/// Email e LastLoginAt sempre aggiornati; DisplayName/PictureUrl solo se forniti (null-coalesce);
/// CreatedAt fissato alla creazione e preservato sugli update. Il repository fa la query, applica
/// il risultato sull'<c>UserEntity</c> e chiama SaveChanges.
/// </summary>
public class UserUpsertService
{
    /// <summary>
    /// Calcola i campi dell'utente a partire dai dati Google e dallo stato esistente
    /// (<c>null</c> = utente non trovato, va creato). <paramref name="utcNow"/> è iniettato
    /// per determinismo nei test.
    /// </summary>
    public UserUpsertResult BuildOrUpdate(GoogleUserUpsert upsert, ExistingUserSnapshot? existing, DateTime utcNow)
    {
        if (existing is null)
        {
            return new UserUpsertResult
            {
                IsNew = true,
                Email = upsert.Email,
                DisplayName = upsert.DisplayName,
                PictureUrl = upsert.PictureUrl,
                CreatedAt = utcNow,
                LastLoginAt = utcNow
            };
        }

        return new UserUpsertResult
        {
            IsNew = false,
            Email = upsert.Email,
            DisplayName = upsert.DisplayName ?? existing.DisplayName,
            PictureUrl = upsert.PictureUrl ?? existing.PictureUrl,
            CreatedAt = existing.CreatedAt,
            LastLoginAt = utcNow
        };
    }
}
