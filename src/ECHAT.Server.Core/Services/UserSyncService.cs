using ECHAT.Server.Core.Interfaces;

namespace ECHAT.Server.Core.Services;

/// <summary>
/// Upsert dell'utente al ritorno dal flusso OAuth: il controller estrae i claim Google,
/// passa qui i campi normalizzati e riceve l'utente persistito. La firma del JWT resta
/// nel controller perché dipende dalla configurazione AspNetCore.
/// </summary>
public class UserSyncService
{
    private readonly IUserStore _users;

    public UserSyncService(IUserStore users)
    {
        _users = users;
    }

    public Task<UserRecord> UpsertGoogleUserAsync(
        string googleSubjectId, string email, string displayName, string? pictureUrl)
    {
        return _users.UpsertGoogleUserAsync(new GoogleUserUpsert
        {
            GoogleSubjectId = googleSubjectId,
            Email = email,
            DisplayName = displayName,
            PictureUrl = pictureUrl
        });
    }
}
