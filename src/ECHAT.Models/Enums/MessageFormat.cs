namespace ECHAT.Models.Enums;

/// <summary>
/// Formato di rendering per la parte testuale di <see cref="ECHAT.Models.Domain.MessagePayload"/>.
/// Sta dentro al payload E2EE: il server non lo vede.
/// </summary>
public enum MessageFormat
{
    /// <summary>Testo semplice. Default; nessun parsing, output con escape.</summary>
    Plain = 0,

    /// <summary>CommonMark + Markdown GitHub-flavored. L'HTML grezzo è disabilitato.</summary>
    Markdown = 1,

    /// <summary>HTML grezzo. Renderizzato in un iframe sandboxed: script, form e popup sono bloccati.</summary>
    Html = 2
}
