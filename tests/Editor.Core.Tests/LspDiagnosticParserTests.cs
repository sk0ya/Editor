using System.Text.Json;
using Editor.Core.Lsp;

namespace Editor.Core.Tests;

public class LspDiagnosticParserTests
{
    /// <summary>Roslyn Language Server が pull（textDocument/diagnostic）で実際に返す形。
    /// 重大度はヒント、種類は Unnecessary、規則名と説明ページが付く——このどれを落としても
    /// 「文面だけ」の診断になり、なぜそう言われたのか辿れなくなる。</summary>
    [Fact]
    public void Roslyn_unnecessary_using_keeps_code_description_and_tag()
    {
        using var json = JsonDocument.Parse("""
        {"range":{"start":{"line":0,"character":0},"end":{"line":8,"character":31}},
         "severity":4,"code":"IDE0005",
         "codeDescription":{"href":"https://learn.microsoft.com/dotnet/fundamentals/code-analysis/style-rules/ide0005"},
         "tags":[1],"source":"Roslyn","message":"Using ディレクティブは必要ありません。"}
        """);

        Assert.True(LspDiagnosticParser.TryParse(json.RootElement, out var diagnostic));
        Assert.Equal(DiagnosticSeverity.Hint, diagnostic.Severity);
        Assert.Equal("IDE0005", diagnostic.Code);
        Assert.Equal("Roslyn", diagnostic.Source);
        Assert.EndsWith("ide0005", diagnostic.CodeDescriptionHref);
        Assert.True(diagnostic.IsUnnecessary);
        Assert.False(diagnostic.IsDeprecated);
        Assert.Equal(new LspPosition(8, 31), diagnostic.Range.End);
    }

    /// <summary>pull と push で読み口が分かれていたころ、pull 側だけ code を捨てていた。
    /// 同じ1件を両方の入口から通して、同じものが出ることを固定する。</summary>
    [Fact]
    public void Pull_and_push_shaped_payloads_parse_identically()
    {
        const string item = """
        {"range":{"start":{"line":2,"character":0},"end":{"line":2,"character":12}},
         "severity":2,"code":"CS0168","message":"変数は宣言されましたが使用されていません"}
        """;

        using var pull = JsonDocument.Parse($$"""{"kind":"full","resultId":"1","items":[{{item}}]}""");
        using var push = JsonDocument.Parse($"[{item}]");

        var fromPull = LspDocumentDiagnosticParser.Parse(pull.RootElement)!.Diagnostics.Single();
        Assert.True(LspDiagnosticParser.TryParse(push.RootElement[0], out var fromPush));

        Assert.Equal(fromPush, fromPull);
        Assert.Equal("CS0168", fromPull.Code);
    }

    /// <summary>tsserver は code を数値で返す。</summary>
    [Fact]
    public void Numeric_code_becomes_text()
    {
        using var json = JsonDocument.Parse("""
        {"range":{"start":{"line":1,"character":4},"end":{"line":1,"character":9}},
         "severity":1,"code":2304,"source":"ts","message":"Cannot find name 'foo'."}
        """);

        Assert.True(LspDiagnosticParser.TryParse(json.RootElement, out var diagnostic));
        Assert.Equal("2304", diagnostic.Code);
        Assert.False(diagnostic.IsUnnecessary);
    }

    [Fact]
    public void Deprecated_tag_is_kept_and_unknown_tags_are_dropped()
    {
        using var json = JsonDocument.Parse("""
        {"range":{"start":{"line":0,"character":0},"end":{"line":0,"character":5}},
         "severity":3,"tags":[2,99,2],"message":"古い API"}
        """);

        Assert.True(LspDiagnosticParser.TryParse(json.RootElement, out var diagnostic));
        Assert.Equal([DiagnosticTag.Deprecated], diagnostic.Tags);
        Assert.True(diagnostic.IsDeprecated);
    }

    /// <summary>range が無い／形が違うものは捨てる（既定の重大度はエラー）。</summary>
    [Theory]
    [InlineData("""{"message":"no range"}""")]
    [InlineData("""[1,2,3]""")]
    [InlineData("\"a string\"")]
    public void Malformed_items_are_rejected(string payload)
    {
        using var json = JsonDocument.Parse(payload);
        Assert.False(LspDiagnosticParser.TryParse(json.RootElement, out _));
    }

    [Fact]
    public void Missing_severity_defaults_to_error()
    {
        using var json = JsonDocument.Parse("""
        {"range":{"start":{"line":0,"character":0},"end":{"line":0,"character":1}},"message":"x"}
        """);

        Assert.True(LspDiagnosticParser.TryParse(json.RootElement, out var diagnostic));
        Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity);
        Assert.Null(diagnostic.Tags);
        Assert.Null(diagnostic.CodeDescriptionHref);
    }
}
