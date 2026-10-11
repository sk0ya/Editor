using System.Text.Json;
using Editor.Controls.Lsp;
using Editor.Core.Lsp;
using Xunit;

namespace Editor.Controls.Tests;

/// <summary>ホストの追加補完候補の合流・置換範囲の補正・workspace/configuration の応答（§36）。</summary>
public class CompletionMergeTests
{
    [Fact]
    public void Merge_AppendsHostItems_AndKeepsLspItemOnSameLabelAndKind()
    {
        var lsp = new[] { new LspCompletionItem("if", CompletionItemKind.Keyword, Detail: "keyword") };
        var host = new[]
        {
            new LspCompletionItem("if", CompletionItemKind.Keyword, Detail: "duplicate"),
            new LspCompletionItem("if", CompletionItemKind.Snippet, Detail: "template"),
            new LspCompletionItem("prop", CompletionItemKind.Snippet),
        };

        var merged = CompletionMerge.Merge(host, lsp);

        Assert.Equal(["keyword", "template", null], merged.Select(i => i.Detail));
    }

    [Fact]
    public void ExtendToCaret_IncludesCharactersTypedAfterTheRequest()
    {
        // `x.` で要求 → 置換範囲は x の先頭〜ドットの直後。その後 `if` を打った。
        var range = new LspRange(new LspPosition(3, 4), new LspPosition(3, 6));
        var extended = CompletionMerge.ExtendToCaret(range, caretLine: 3, caretColumn: 8);
        Assert.Equal(new LspPosition(3, 8), extended.End);
        Assert.Equal(new LspPosition(3, 4), extended.Start);
    }

    [Fact]
    public void ExtendToCaret_LeavesRangesThatAlreadyReachTheCaret()
    {
        var range = new LspRange(new LspPosition(0, 0), new LspPosition(0, 10));
        Assert.Equal(range, CompletionMerge.ExtendToCaret(range, 0, 5));
        var otherLine = new LspRange(new LspPosition(1, 0), new LspPosition(1, 2));
        Assert.Equal(otherLine, CompletionMerge.ExtendToCaret(otherLine, 2, 5));
    }

    [Fact]
    public async Task Start_SwallowsHostFailures()
    {
        var items = await CompletionMerge.Start((_, _, _) => throw new InvalidOperationException(), 0, 0, default);
        Assert.Empty(items);
    }

    [Fact]
    public void AnswerConfiguration_ReturnsOneValuePerItemInOrder()
    {
        using var json = JsonDocument.Parse("""
            {"items":[
              {"section":"csharp|inlay_hints.dotnet_enable_inlay_hints_for_parameters"},
              {"section":"unknown.thing"},
              {"scopeUri":"file:///c:/x.cs"}
            ]}
            """);

        var result = LspClient.AnswerConfiguration(
            (section, _) => section?.EndsWith("for_parameters", StringComparison.Ordinal) == true ? "true" : null,
            json.RootElement);

        Assert.Equal(3, result.Length);
        Assert.Equal("true", result[0]);
        Assert.Null(result[1]);
        Assert.Null(result[2]);
    }

    [Fact]
    public void AnswerConfiguration_ProviderExceptionBecomesNull()
    {
        using var json = JsonDocument.Parse("""{"items":[{"section":"a"}]}""");
        var result = LspClient.AnswerConfiguration((_, _) => throw new Exception(), json.RootElement);
        Assert.Equal([null], result);
    }
}
