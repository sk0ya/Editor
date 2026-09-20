using Editor.Core.Lsp;
using Editor.Core.Text;

namespace Editor.Core.Tests;

/// <summary>ホバーのポップアップを文字として持ち出せること（中身は <c>TextBlock</c> なので選択できない）。</summary>
public class HoverCopyTextTests
{
    [Fact]
    public void Build_TakesTheSignatureWithoutFenceMarkers()
    {
        var text = HoverCopyText.Build([], HoverMarkdown.Parse("```csharp\nint Count { get; }\n```"));

        Assert.Equal("int Count { get; }", text);
    }

    /// <summary>画面と同じ読み順——診断 → シグネチャ → 説明。</summary>
    [Fact]
    public void Build_KeepsTheOrderShownInThePopup()
    {
        var diagnostic = new LspDiagnostic(
            new LspRange(new LspPosition(0, 4), new LspPosition(0, 9)),
            "値が使われていません", DiagnosticSeverity.Warning, "Roslyn", "CS0219");

        var text = HoverCopyText.Build(
            [diagnostic], HoverMarkdown.Parse("```csharp\nint count\n```\n\nローカル変数です。"));

        Assert.Equal(
            "値が使われていません  Roslyn(CS0219)\n\nint count\n\nローカル変数です。", text);
    }

    /// <summary>区切り線は絵。文字にすると本文に混ざるだけなので落とす。</summary>
    [Fact]
    public void Build_DropsHorizontalRules()
    {
        var text = HoverCopyText.Build([], HoverMarkdown.Parse("上\n\n---\n\n下"));

        Assert.Equal("上\n\n下", text);
    }

    /// <summary>ホストが添えた「なぜ」も写しに入る（見えているものと写しをずらさない）。</summary>
    [Fact]
    public void Build_KeepsTheHostExplanationUnderItsDiagnostic()
    {
        var diagnostic = new LspDiagnostic(
            new LspRange(new LspPosition(0, 0), new LspPosition(0, 21)),
            "Using ディレクティブは必要ありません。", DiagnosticSeverity.Hint, "Roslyn", "IDE0005");

        var text = HoverCopyText.Build(
            [diagnostic], [], ["GlobalUsings.cs:14 の global using と重複しています。"]);

        Assert.Equal(
            "Using ディレクティブは必要ありません。  Roslyn(IDE0005)\n" +
            "GlobalUsings.cs:14 の global using と重複しています。", text);
    }

    [Fact]
    public void Build_WithoutExplanations_IsUnchanged()
    {
        var diagnostic = new LspDiagnostic(
            new LspRange(new LspPosition(0, 0), new LspPosition(0, 1)),
            "メッセージ", DiagnosticSeverity.Hint, "Roslyn", "IDE0005");

        Assert.Equal(
            HoverCopyText.Build([diagnostic], []),
            HoverCopyText.Build([diagnostic], [], [null]));
    }

    [Fact]
    public void Build_WithNothingToShow_IsEmpty() => Assert.Equal("", HoverCopyText.Build([], []));

    [Theory]
    [InlineData(null, null, "")]
    [InlineData("Roslyn", null, "Roslyn")]
    [InlineData(null, "CS0219", "CS0219")]
    [InlineData("Roslyn", "CS0219", "Roslyn(CS0219)")]
    public void Origin_SpellsTheSourceAndCodeTheWayThePopupDoes(
        string? source, string? code, string expected)
    {
        var diagnostic = new LspDiagnostic(
            new LspRange(new LspPosition(0, 0), new LspPosition(0, 1)),
            "メッセージ", DiagnosticSeverity.Warning, source, code);

        Assert.Equal(expected, HoverCopyText.Origin(diagnostic));
    }
}
