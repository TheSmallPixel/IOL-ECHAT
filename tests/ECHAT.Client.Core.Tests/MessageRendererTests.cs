using ECHAT.Client.Core.Services;
using FluentAssertions;

namespace ECHAT.Client.Core.Tests;

/// <summary>
/// I metodi di auto-detection (<c>DetectLanguage</c>/<c>AutoDetectFences</c>) sono <c>internal</c>
/// e qui non c'è <c>InternalsVisibleTo</c>: li esercitiamo attraverso il pubblico
/// <see cref="MessageRenderer.MarkdownToHtml"/>, verificando il tag <c>language-xxx</c> emesso da
/// Markdig sul fence ri-taggato.
/// </summary>
public class MessageRendererTests
{
    private readonly MessageRenderer _sut = new();

    // ---- MarkdownToHtml basics ----

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    public void MarkdownToHtml_EmptyOrNull_ReturnsEmpty(string? input)
    {
        _sut.MarkdownToHtml(input!).Should().BeEmpty();
    }

    [Fact]
    public void MarkdownToHtml_BasicMarkdown_EmitsHtml()
    {
        var html = _sut.MarkdownToHtml("**bold** and *italic*");

        html.Should().Contain("<strong>bold</strong>");
        html.Should().Contain("<em>italic</em>");
    }

    [Fact]
    public void MarkdownToHtml_RawScriptTag_NotEmitted_BecauseHtmlDisabled()
    {
        var html = _sut.MarkdownToHtml("hello <script>alert(1)</script> world");

        // DisableHtml() rimuove i tag raw dal sorgente: niente <script> nell'output.
        html.Should().NotContain("<script>");
        html.Should().Contain("hello");
        html.Should().Contain("world");
    }

    [Fact]
    public void MarkdownToHtml_RawIframeTag_NotEmitted_BecauseHtmlDisabled()
    {
        var html = _sut.MarkdownToHtml("before <iframe src=\"evil\"></iframe> after");

        html.Should().NotContain("<iframe");
        html.Should().Contain("before");
        html.Should().Contain("after");
    }

    [Fact]
    public void MarkdownToHtml_SoftLineBreak_BecomesBr()
    {
        // UseSoftlineBreakAsHardlineBreak(): un newline singolo dentro un paragrafo  <br>.
        var html = _sut.MarkdownToHtml("line one\nline two");

        html.Should().Contain("<br");
        html.Should().Contain("line one");
        html.Should().Contain("line two");
    }

    // ---- Auto-detection del linguaggio nei fence senza info-string ----

    [Theory]
    [InlineData("{\n  \"a\": 1\n}", "json")]       // primo char {
    [InlineData("[\n  1, 2\n]", "json")]            // primo char [
    public void MarkdownToHtml_BareFence_Json_TaggedJson(string body, string expectedLang)
    {
        var html = _sut.MarkdownToHtml(Fence(body));
        html.Should().Contain($"language-{expectedLang}");
    }

    [Fact]
    public void MarkdownToHtml_BareFence_Html_TaggedHtml()
    {
        var html = _sut.MarkdownToHtml(Fence("<div class=\"x\">hi</div>"));
        html.Should().Contain("language-html");
    }

    [Fact]
    public void MarkdownToHtml_BareFence_Yaml_TaggedYaml()
    {
        // Due righe "chiave: valore"  yaml (e non sembra JSON, non parte con { o [).
        var html = _sut.MarkdownToHtml(Fence("name: echat\nversion: 2"));
        html.Should().Contain("language-yaml");
    }

    [Theory]
    [InlineData("using System;\nConsole.WriteLine();")]
    [InlineData("namespace Foo;\nclass Bar { }")]
    public void MarkdownToHtml_BareFence_CSharp_TaggedCsharp(string body)
    {
        var html = _sut.MarkdownToHtml(Fence(body));
        html.Should().Contain("language-csharp");
    }

    [Theory]
    [InlineData("def greet():\n    pass")]
    [InlineData("import os\nx = 1")]
    [InlineData("x = 1\nprint(x)")]
    public void MarkdownToHtml_BareFence_Python_TaggedPython(string body)
    {
        var html = _sut.MarkdownToHtml(Fence(body));
        html.Should().Contain("language-python");
    }

    [Theory]
    [InlineData("function f() {}")]
    [InlineData("const x = 1;")]
    [InlineData("items.map(x => x)")]
    public void MarkdownToHtml_BareFence_JavaScript_TaggedJavascript(string body)
    {
        var html = _sut.MarkdownToHtml(Fence(body));
        html.Should().Contain("language-javascript");
    }

    [Theory]
    [InlineData("# Title\nsome prose")]
    [InlineData("- item one\n- item two")]
    public void MarkdownToHtml_BareFence_Markdown_TaggedMarkdown(string body)
    {
        var html = _sut.MarkdownToHtml(Fence(body));
        html.Should().Contain("language-markdown");
    }

    [Fact]
    public void MarkdownToHtml_AlreadyTaggedFence_LeftUntouched()
    {
        // Il fence ha già un'info-string: la regex BareFence non lo tocca, quindi resta python.
        var md = "```python\nprint('hi')\n```";
        var html = _sut.MarkdownToHtml(md);

        html.Should().Contain("language-python");
        html.Should().NotContain("language-csharp");
    }

    [Fact]
    public void MarkdownToHtml_BareFence_UnrecognizableBody_StaysUntagged()
    {
        // Prosa che non matcha nessuna euristica  DetectLanguage ritorna ""  fence resta nudo.
        var html = _sut.MarkdownToHtml(Fence("just some plain words here"));
        html.Should().NotContain("language-");
    }

    [Fact]
    public void MarkdownToHtml_NoFence_ProseUntouched()
    {
        var html = _sut.MarkdownToHtml("plain paragraph with no code");
        html.Should().NotContain("<code");
        html.Should().Contain("plain paragraph");
    }

    private static string Fence(string body) => $"```\n{body}\n```";
}
