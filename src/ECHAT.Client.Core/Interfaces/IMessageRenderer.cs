namespace ECHAT.Client.Core.Interfaces;

/// <summary>
/// Rendering della parte testuale del messaggio. Markdown -> HTML sanificato, con riconoscimento
/// automatico del linguaggio dei blocchi di codice fenced senza info-string.
/// </summary>
public interface IMessageRenderer
{
    /// <summary>Markdown -> HTML sanificato, pronto per <c>MarkupString</c>.</summary>
    string MarkdownToHtml(string markdown);
}
