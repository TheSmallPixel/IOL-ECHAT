using System.Text.RegularExpressions;
using ECHAT.Client.Core.Interfaces;
using Markdig;

namespace ECHAT.Client.Core.Services;

/// <summary>
/// Renderizza la parte testuale del messaggio in Markdown -> HTML sanificato.
/// Markdig con HTML raw disabilitato, soft-line-break convertito in br, auto-link e task list da
/// <c>UseAdvancedExtensions</c>; l'output è sicuro da passare a <c>MarkupString</c>. Per i blocchi
/// di codice fenced senza linguaggio si tenta un riconoscimento automatico e si re-tagga il fence,
/// così Markdig emette <c>&lt;pre&gt;&lt;code class="language-xxx"&gt;</c>; la colorazione vera
/// arriva lato client da <c>highlight.js</c> in <c>MessageBubble.OnAfterRenderAsync</c>.
/// </summary>
public class MessageRenderer : IMessageRenderer
{
    private static readonly MarkdownPipeline MarkdownPipeline = new MarkdownPipelineBuilder()
        .UseAdvancedExtensions()        // tabelle, task list, auto-link, footnote, ecc.
        .UseSoftlineBreakAsHardlineBreak()
        .DisableHtml()                  // rimuove dal sorgente tag raw come <script>, <iframe>, <style>
        .Build();

    /// <summary>Markdown -> HTML sanificato, pronto per <c>MarkupString</c>.</summary>
    public string MarkdownToHtml(string markdown)
        => string.IsNullOrEmpty(markdown)
            ? string.Empty
            : Markdown.ToHtml(AutoDetectFences(markdown), MarkdownPipeline);

    // ---- Riconoscimento linguaggio per i fence ``` senza tag --------------------------------------

    // Cattura un blocco di codice fenced con info-string vuota: indent opzionale, ``` (3+ apici),
    // whitespace opzionale, newline, body, poi il fence di chiusura corrispondente. Gruppi:
    //   1: fence di apertura (la sequenza di ```)
    //   2: body
    //   3: fence di chiusura
    private static readonly Regex BareFenceRegex = new(
        @"(?m)^(```+)[ \t]*\r?\n([\s\S]*?)\r?\n(\1)[ \t]*$",
        RegexOptions.Compiled);

    private static readonly Regex YamlKeyRegex = new(
        @"^[A-Za-z_][\w-]*\s*:\s*\S",
        RegexOptions.Compiled | RegexOptions.Multiline);

    /// <summary>
    /// Pre-processa il Markdown: ai blocchi fenced senza info-string aggiunge un tag di linguaggio
    /// indovinato. Le euristiche sono volutamente leggere, perché questo gira a ogni render. Tocchiamo
    /// solo i fence <c>```</c> senza linguaggio; una volta taggati, ci pensa highlight.js.
    /// </summary>
    internal static string AutoDetectFences(string markdown)
    {
        if (string.IsNullOrEmpty(markdown) || !markdown.Contains("```"))
            return markdown;

        return BareFenceRegex.Replace(markdown, m =>
        {
            var fence = m.Groups[1].Value;
            var body = m.Groups[2].Value;
            var closing = m.Groups[3].Value;
            var lang = DetectLanguage(body);
            return string.IsNullOrEmpty(lang)
                ? m.Value
                : $"{fence}{lang}\n{body}\n{closing}";
        });
    }

    internal static string DetectLanguage(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return string.Empty;

        // Prima riga non vuota + primo carattere non-whitespace su quella riga.
        string? firstLine = null;
        char firstChar = '\0';
        foreach (var raw in body.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (string.IsNullOrWhiteSpace(line)) continue;
            firstLine = line;
            foreach (var c in line)
            {
                if (!char.IsWhiteSpace(c)) { firstChar = c; break; }
            }
            break;
        }
        if (firstLine == null) return string.Empty;

        // JSON: il primo carattere non-whitespace è { o [
        if (firstChar == '{' || firstChar == '[')
            return "json";

        // HTML: presenza di tag comuni (case-insensitive)
        if (ContainsAnyIgnoreCase(body,
                "<html", "<!doctype", "<div", "<span", "<p>", "<a ", "<body", "<head"))
            return "html";

        // YAML: 2+ righe "chiave: valore" e non sembra JSON
        if (YamlKeyRegex.Matches(body).Count >= 2)
            return "yaml";

        // C#
        if (body.Contains("using System") ||
            body.Contains("namespace ") ||
            body.Contains("public class ") ||
            body.Contains("public static "))
            return "csharp";

        // Python
        if (HasLineStartingWith(body, "def ") ||
            HasLineStartingWith(body, "import ") ||
            Regex.IsMatch(body, @"^\s*from\s+\w+\s+import\s", RegexOptions.Multiline) ||
            body.Contains("print("))
            return "python";

        // JavaScript
        if (body.Contains("function ") ||
            body.Contains("const ") ||
            body.Contains("let ") ||
            body.Contains("=>") ||
            body.Contains("console.log(") ||
            body.Contains("document."))
            return "javascript";

        // Markdown: solo come ultima opzione, e solo se il body sembra prosa + liste.
        if (body.StartsWith("# ") || body.StartsWith("## ") || CountLineStartingWith(body, "- ") >= 2)
            return "markdown";

        return string.Empty;
    }

    private static bool ContainsAnyIgnoreCase(string body, params string[] needles)
    {
        foreach (var n in needles)
        {
            if (body.IndexOf(n, StringComparison.OrdinalIgnoreCase) >= 0) return true;
        }
        return false;
    }

    private static bool HasLineStartingWith(string body, string prefix)
    {
        // True se una qualsiasi riga, dopo aver tolto whitespace iniziale, parte con il prefisso.
        foreach (var raw in body.Split('\n'))
        {
            var line = raw.TrimStart();
            if (line.StartsWith(prefix, StringComparison.Ordinal)) return true;
        }
        return false;
    }

    private static int CountLineStartingWith(string body, string prefix)
    {
        var count = 0;
        foreach (var raw in body.Split('\n'))
        {
            var line = raw.TrimStart();
            if (line.StartsWith(prefix, StringComparison.Ordinal)) count++;
        }
        return count;
    }
}
