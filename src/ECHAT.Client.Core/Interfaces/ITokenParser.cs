using System.Security.Claims;

namespace ECHAT.Client.Core.Interfaces;

/// <summary>
/// Parsing puro (senza I/O) di token JWT: estrazione claim, userId e check di scadenza.
/// Tutta la logica testabile vive qui; l'App ci appoggia sopra l'interop con localStorage e
/// il broadcast dello stato di autenticazione.
/// </summary>
public interface ITokenParser
{
    /// <summary>
    /// Restituisce i claim del token se valido e non scaduto. Token malformato o scaduto -> vuoto.
    /// </summary>
    IEnumerable<Claim> ParseClaimsFromJwt(string jwt);

    /// <summary>
    /// Estrae lo userId dal token: NameIdentifier se presente, altrimenti il Subject, altrimenti
    /// "(unknown)". Token non parsabile -> "(unparseable)".
    /// </summary>
    string TryExtractUserId(string jwt);

    /// <summary>True se l'istante di scadenza è nel passato rispetto a UtcNow.</summary>
    bool IsTokenExpired(DateTime validTo);
}
