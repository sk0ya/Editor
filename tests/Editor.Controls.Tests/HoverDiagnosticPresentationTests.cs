using System.Windows.Documents;
using System.Windows.Media;
using Editor.Controls.HostIntegration;
using Editor.Controls.Rendering;
using Editor.Controls.Themes;
using Editor.Core.Lsp;
using Editor.Core.Text;

namespace Editor.Controls.Tests;

/// <summary>診断のホバーが「なぜそう言われたのか」を出せること。文面だけでは
/// 「使っているのに不要と言われた」に見える診断があり、出どころ・説明ページ・ホストの補記が
/// その差を埋める。</summary>
public class HoverDiagnosticPresentationTests
{
    private static LspDiagnostic UnnecessaryUsing() =>
        new(new LspRange(new LspPosition(0, 0), new LspPosition(0, 21)),
            "Using ディレクティブは必要ありません。", DiagnosticSeverity.Hint, "Roslyn", "IDE0005",
            "https://learn.microsoft.com/dotnet/fundamentals/code-analysis/style-rules/ide0005",
            [DiagnosticTag.Unnecessary]);

    [Fact]
    public void Origin_is_a_link_to_the_rule_page()
    {
        WpfTestHost.Run(() =>
        {
            string? opened = null;
            var content = HoverContentBuilder.Build(
                [UnnecessaryUsing()], [], EditorTheme.Dracula, new FontFamily("Consolas"), 12, null,
                default, null, url => opened = url);

            var link = FindHyperlink(content);
            Assert.NotNull(link);
            Assert.Equal("Roslyn(IDE0005)", new TextRange(link!.ContentStart, link.ContentEnd).Text);

            link.RaiseEvent(new System.Windows.RoutedEventArgs(Hyperlink.ClickEvent));
            Assert.EndsWith("ide0005", opened);
        });
    }

    /// <summary>説明ページを開く口が無いときは、ただの文字として出す（押せない飾りを作らない）。</summary>
    [Fact]
    public void Origin_without_an_opener_stays_plain_text()
    {
        WpfTestHost.Run(() =>
        {
            var content = HoverContentBuilder.Build(
                [UnnecessaryUsing()], [], EditorTheme.Dracula, new FontFamily("Consolas"), 12, null);

            Assert.Null(FindHyperlink(content));
            Assert.Contains("Roslyn(IDE0005)", DocumentText(content));
        });
    }

    [Fact]
    public void Host_explanation_is_shown_under_the_message()
    {
        WpfTestHost.Run(() =>
        {
            var content = HoverContentBuilder.Build(
                [UnnecessaryUsing()], [], EditorTheme.Dracula, new FontFamily("Consolas"), 12, null,
                default, ["GlobalUsings.cs:14 の global using と重複しています。"]);

            var text = DocumentText(content);
            Assert.Contains("Using ディレクティブは必要ありません。", text);
            Assert.Contains("GlobalUsings.cs:14 の global using と重複しています。", text);
        });
    }

    /// <summary>ホストの補記は<b>ポップアップを出してから</b>埋まる（解析を待って表示自体を
    /// 遅らせない）。実物の制御を通して、届いた補記が組み直しで出ることを確かめる。</summary>
    [Fact]
    public void Host_explanation_reaches_the_popup_through_the_control()
    {
        LspCompletionTestHarness.WithEditor(vimEnabled: false, (editor, lsp) =>
        {
            lsp.HoverResult = "```csharp\nnamespace System.Windows\n```";
            editor.SetText("using System.Windows;\n");
            editor.ReplaceDiagnostics([
                new EditorDiagnostic(
                    EditorTextRange.Create(0, 0, 0, 21),
                    "Using ディレクティブは必要ありません。",
                    EditorDiagnosticSeverity.Hint,
                    "Roslyn",
                    "IDE0005",
                    null,
                    "https://learn.microsoft.com/dotnet/fundamentals/code-analysis/style-rules/ide0005",
                    [EditorDiagnosticTag.Unnecessary]),
            ]);

            var hover = editor.ShowHoverInfoForTestAsync(0, 6);
            LspCompletionTestHarness.Pump();
            LspCompletionTestHarness.Pump();

            Assert.True(hover.IsCompleted);
            var content = editor.HoverPopupContentForTest;
            Assert.NotNull(content);
            var text = DocumentText(content!);
            // ホスト診断の素性（規則名）も、LSP 由来と同じように出る。
            Assert.Contains("Roslyn(IDE0005)", text);
            Assert.Contains("GlobalUsings.cs:14 の global using と重複しています。", text);
        },
        lsp => new VimEditorControlOptions
        {
            LspViewFactory = () => lsp,
            HostDiagnosticExplanationProvider = (_, _, diagnostic, _) =>
                Task.FromResult<string?>(diagnostic.Code == "IDE0005"
                    ? "GlobalUsings.cs:14 の global using と重複しています。"
                    : null),
        });
    }

    private static Hyperlink? FindHyperlink(FlowDocument document) =>
        document.Blocks.OfType<Paragraph>()
            .SelectMany(paragraph => paragraph.Inlines)
            .OfType<Hyperlink>()
            .FirstOrDefault();

    private static string DocumentText(FlowDocument document) =>
        new TextRange(document.ContentStart, document.ContentEnd).Text;
}
