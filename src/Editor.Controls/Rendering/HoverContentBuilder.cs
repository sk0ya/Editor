using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using Editor.Controls.Themes;
using Editor.Core.Lsp;
using Editor.Core.Syntax;
using Editor.Core.Text;

namespace Editor.Controls.Rendering;

/// <summary>
/// 本文ホバーのポップアップ中身を組み立てる。上から「診断（エラー・警告）」「その場で押せる修正
/// （quickfix）」「シグネチャ（コードフェンス）」「説明」の順で、Rider のツールチップと同じ読み順にする。
///
/// <para>診断があるときは、まず<b>電球だけ</b>を出す（候補を数件積み上げると、型を読みに来ただけの人の
/// 視界を塞ぐ）。電球を押すと候補が開き、候補は<b>押せる</b>——Alt+Enter へ持ち替えずにそのまま直せる。
/// 候補はそのとき初めて問い合わせるので、開いた直後は「取得しています」、答えが空なら「候補はありません」を
/// 出す（押しても何も起きない電球にしない）。開閉は <see cref="HoverFixSection.OnToggle"/>、
/// 適用は <see cref="HoverFixSection.OnApply"/> でホスト（<c>VimEditorControl</c>）へ返す。</para>
///
/// <para>コードフェンスは<b>エディタ本体と同じシンタックス着色</b>を通す（情報文字列 <c>csharp</c> を
/// 拡張子 <c>.cs</c> に読み替えて既存の <see cref="SyntaxEngine"/> に食わせるだけ）。同じ色で出ないと、
/// ポップアップの中のシグネチャがコードに見えない。</para>
/// </summary>
internal static class HoverContentBuilder
{
    /// <summary>フェンスの情報文字列 → 既存のシンタックス定義を引くための拡張子。</summary>
    private static readonly Dictionary<string, string> LanguageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ["csharp"] = ".cs", ["c#"] = ".cs", ["cs"] = ".cs",
        ["typescript"] = ".ts", ["ts"] = ".ts", ["tsx"] = ".ts",
        ["javascript"] = ".js", ["js"] = ".js", ["jsx"] = ".js",
        ["python"] = ".py", ["py"] = ".py",
        ["rust"] = ".rs", ["rs"] = ".rs",
        ["go"] = ".go", ["golang"] = ".go",
        ["json"] = ".json", ["jsonc"] = ".json",
        ["yaml"] = ".yaml", ["yml"] = ".yaml",
        ["toml"] = ".toml",
        ["sh"] = ".sh", ["bash"] = ".sh", ["shell"] = ".sh", ["zsh"] = ".sh",
        ["powershell"] = ".ps1", ["ps1"] = ".ps1", ["pwsh"] = ".ps1",
        ["css"] = ".css", ["scss"] = ".css",
        ["sql"] = ".sql",
        ["cpp"] = ".cpp", ["c++"] = ".cpp", ["c"] = ".c", ["h"] = ".h",
        ["lua"] = ".lua",
        ["ruby"] = ".rb", ["rb"] = ".rb",
        ["xml"] = ".xml", ["html"] = ".xml",
        ["markdown"] = ".md", ["md"] = ".md",
    };

    public static UIElement Build(
        IReadOnlyList<LspDiagnostic> diagnostics,
        IReadOnlyList<HoverBlock> blocks,
        EditorTheme theme,
        FontFamily monoFont,
        double fontSize,
        SyntaxLanguageRegistry? languages,
        HoverFixSection fixes = default)
    {
        var stack = new StackPanel();

        foreach (var diagnostic in diagnostics)
            stack.Children.Add(BuildDiagnostic(diagnostic, theme, fontSize));

        if (fixes.Show && fixes.OnApply is not null)
            foreach (var element in BuildFixes(fixes, theme, fontSize))
                stack.Children.Add(element);

        if (diagnostics.Count > 0 && blocks.Count > 0)
            stack.Children.Add(BuildSeparator(theme));

        foreach (var block in blocks)
        {
            switch (block.Kind)
            {
                case HoverBlockKind.Code:
                    stack.Children.Add(BuildCode(block, theme, monoFont, fontSize, languages));
                    break;
                case HoverBlockKind.Rule:
                    stack.Children.Add(BuildSeparator(theme));
                    break;
                default:
                    stack.Children.Add(BuildText(block, theme, monoFont, fontSize));
                    break;
            }
        }

        return stack;
    }

    private static UIElement BuildDiagnostic(LspDiagnostic diagnostic, EditorTheme theme, double fontSize)
    {
        var brush = diagnostic.Severity switch
        {
            DiagnosticSeverity.Error => theme.DiagnosticError,
            DiagnosticSeverity.Warning => theme.DiagnosticWarning,
            DiagnosticSeverity.Information => theme.DiagnosticInfo,
            _ => theme.DiagnosticHint,
        };

        var text = new TextBlock
        {
            FontSize = fontSize,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 2),
        };
        // 重大度は色の付いた丸で示す（アイコンフォントに依存しない）。
        text.Inlines.Add(new Run("● ") { Foreground = brush });
        text.Inlines.Add(new Run(diagnostic.Message) { Foreground = theme.Foreground });

        var origin = Origin(diagnostic);
        if (origin.Length > 0)
            text.Inlines.Add(new Run("  " + origin) { Foreground = theme.LineNumberFg });

        return text;
    }

    private static string Origin(LspDiagnostic diagnostic) => (diagnostic.Source, diagnostic.Code) switch
    {
        (null or "", null or "") => "",
        (null or "", var code) => code!,
        (var source, null or "") => source!,
        var (source, code) => $"{source}({code})",
    };

    /// <summary>電球と、開いているときの中身（取得中／候補なし／候補の行）。</summary>
    private static IEnumerable<UIElement> BuildFixes(
        HoverFixSection fixes, EditorTheme theme, double fontSize)
    {
        // 件数は<b>数え終わってから</b>だけ出す。押す前に問い合わせない以上、そこに数字は無い。
        yield return BuildFixToggle(
            fixes.Loaded ? fixes.Fixes?.Count ?? 0 : null, fixes.Expanded, theme, fontSize, fixes.OnToggle);
        if (!fixes.Expanded) yield break;

        if (fixes.Loading)
        {
            yield return Muted("候補を取得しています…", theme, fontSize);
            yield break;
        }
        if (fixes.Fixes is not { Count: > 0 })
        {
            yield return Muted("修正候補はありません。", theme, fontSize);
            yield break;
        }

        foreach (var fix in fixes.Fixes)
            yield return BuildFix(fix, theme, fontSize, fixes.OnApply!);
        if (fixes.Hidden > 0)
            yield return Muted($"… 他 {fixes.Hidden} 件", theme, fontSize);
    }

    private static UIElement Muted(string text, EditorTheme theme, double fontSize) => new TextBlock
    {
        Text = text,
        FontSize = fontSize,
        Foreground = theme.LineNumberFg,
        Margin = new Thickness(4, 1, 0, 1),
    };

    /// <summary>電球。押すと候補の開閉を切り替える（既定は閉じたまま——読みに来ただけの人を邪魔しない）。
    /// <paramref name="count"/> は数え終わっていれば件数、まだなら null。</summary>
    private static UIElement BuildFixToggle(
        int? count, bool expanded, EditorTheme theme, double fontSize, Action? onToggle)
    {
        var label = new TextBlock { FontSize = fontSize };
        label.Inlines.Add(new Run("💡") { Foreground = theme.Foreground });
        if (count is { } n) label.Inlines.Add(new Run($" {n}") { Foreground = theme.LineNumberFg });

        var row = new Border
        {
            Child = label,
            Tag = FixToggleTag,
            HorizontalAlignment = HorizontalAlignment.Left,
            Padding = new Thickness(5, 1, 7, 2),
            Margin = new Thickness(0, 2, 0, 2),
            CornerRadius = new CornerRadius(3),
            Background = expanded ? theme.CurrentLineBg : Brushes.Transparent,
            BorderBrush = theme.IndentGuideBrush,
            BorderThickness = new Thickness(1),
            Cursor = System.Windows.Input.Cursors.Hand,
        };
        if (onToggle is null) return row;

        row.MouseEnter += (_, _) => row.Background = theme.CurrentLineBg;
        row.MouseLeave += (_, _) => row.Background = expanded ? theme.CurrentLineBg : Brushes.Transparent;
        // Handled にして、ポップアップ全体の「クリックで閉じる」より先にここで受け取る。
        row.MouseLeftButtonUp += (_, e) => { e.Handled = true; onToggle(); };
        return row;
    }

    /// <summary>電球の目印（テストから引くための <see cref="FrameworkElement.Tag"/>）。</summary>
    internal const string FixToggleTag = "hover-fix-toggle";

    /// <summary>押せる修正 1 件。<see cref="FrameworkElement.Tag"/> にアクションを載せてあるので、
    /// テストからも「どの行がどの修正か」を辿れる。</summary>
    private static UIElement BuildFix(
        LspCodeAction action, EditorTheme theme, double fontSize, Action<LspCodeAction> onApply)
    {
        var label = new TextBlock
        {
            Text = "💡 " + action.Title,
            FontSize = fontSize,
            TextWrapping = TextWrapping.Wrap,
            Foreground = action.DisabledReason is { Length: > 0 } ? theme.LineNumberFg : theme.LinkColor,
        };
        var row = new Border
        {
            Child = label,
            Tag = action,
            Padding = new Thickness(4, 2, 4, 3),
            Margin = new Thickness(-4, 1, -4, 1),
            CornerRadius = new CornerRadius(3),
            Background = Brushes.Transparent,
            Cursor = System.Windows.Input.Cursors.Hand,
        };
        row.MouseEnter += (_, _) => row.Background = theme.CurrentLineBg;
        row.MouseLeave += (_, _) => row.Background = Brushes.Transparent;
        // Handled にして、ポップアップ全体の「クリックで閉じる」より先にここで受け取る。
        row.MouseLeftButtonUp += (_, e) => { e.Handled = true; onApply(action); };
        return row;
    }

    private static UIElement BuildSeparator(EditorTheme theme) => new Border
    {
        Height = 1,
        Background = theme.IndentGuideBrush,
        Margin = new Thickness(0, 5, 0, 5),
    };

    private static UIElement BuildText(HoverBlock block, EditorTheme theme, FontFamily monoFont, double fontSize)
    {
        var text = new TextBlock
        {
            FontSize = fontSize,
            TextWrapping = TextWrapping.Wrap,
            Foreground = theme.Foreground,
            Margin = new Thickness(0, 0, 0, 2),
        };

        foreach (var span in block.Spans)
        {
            if (span.Style == HoverSpanStyle.LineBreak) { text.Inlines.Add(new LineBreak()); continue; }
            var run = new Run(span.Text);
            switch (span.Style)
            {
                case HoverSpanStyle.Bold: run.FontWeight = FontWeights.SemiBold; break;
                case HoverSpanStyle.Italic: run.FontStyle = FontStyles.Italic; break;
                case HoverSpanStyle.Code:
                    run.FontFamily = monoFont;
                    run.Foreground = theme.TokenString;
                    break;
            }
            text.Inlines.Add(run);
        }

        return text;
    }

    private static UIElement BuildCode(
        HoverBlock block, EditorTheme theme, FontFamily monoFont, double fontSize,
        SyntaxLanguageRegistry? languages)
    {
        var lines = block.Code.Replace("\r\n", "\n").Split('\n');
        var tokens = Tokenize(lines, block.Language, languages);

        var text = new TextBlock
        {
            FontFamily = monoFont,
            FontSize = fontSize,
            Foreground = theme.Foreground,
            TextWrapping = TextWrapping.Wrap,
        };

        for (var i = 0; i < lines.Length; i++)
        {
            if (i > 0) text.Inlines.Add(new LineBreak());
            AppendCodeLine(text, lines[i], tokens is null || i >= tokens.Length ? null : tokens[i].Tokens, theme);
        }

        return new Border
        {
            Background = theme.LineNumberBg,
            BorderBrush = theme.IndentGuideBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(3),
            Padding = new Thickness(7, 4, 7, 5),
            Margin = new Thickness(0, 0, 0, 3),
            Child = text,
        };
    }

    private static void AppendCodeLine(TextBlock text, string line, SyntaxToken[]? tokens, EditorTheme theme)
    {
        if (tokens is null || tokens.Length == 0)
        {
            text.Inlines.Add(new Run(line));
            return;
        }

        var column = 0;
        foreach (var token in tokens)
        {
            if (token.StartColumn > line.Length) break;
            if (token.StartColumn > column)
                text.Inlines.Add(new Run(line[column..token.StartColumn]));
            var end = Math.Min(line.Length, token.StartColumn + token.Length);
            if (end <= token.StartColumn) continue;
            text.Inlines.Add(new Run(line[token.StartColumn..end])
            {
                Foreground = theme.GetTokenBrush(token.Kind),
            });
            column = end;
        }
        if (column < line.Length) text.Inlines.Add(new Run(line[column..]));
    }

    private static LineTokens[]? Tokenize(string[] lines, string? language, SyntaxLanguageRegistry? languages)
    {
        if (language is null || !LanguageExtensions.TryGetValue(language, out var extension)) return null;
        try
        {
            var engine = new SyntaxEngine(languages);
            engine.DetectLanguage("hover" + extension);
            var tokens = engine.Tokenize(lines);
            return tokens.Length == 0 ? null : tokens;
        }
        catch
        {
            // 着色はあくまで飾り。失敗しても素のテキストで出す。
            return null;
        }
    }
}

/// <summary>ホバーのポップアップに出す「修正」欄の状態。<see cref="Show"/> は電球を出すか
/// （＝その位置に診断があるか）で、候補（<see cref="Fixes"/>）は<b>電球が押されてから</b>入る。</summary>
/// <param name="Show">電球を出すか。</param>
/// <param name="Expanded">候補を開いているか。</param>
/// <param name="Loading">候補を問い合わせ中か。</param>
/// <param name="Loaded">問い合わせが終わったか（終わって 0 件なら「候補はありません」）。</param>
/// <param name="Fixes">表示する候補。</param>
/// <param name="Hidden">上限を超えて出せなかった件数。</param>
/// <param name="OnToggle">電球が押されたとき。</param>
/// <param name="OnApply">候補が押されたとき。</param>
internal readonly record struct HoverFixSection(
    bool Show,
    bool Expanded,
    bool Loading,
    bool Loaded,
    IReadOnlyList<LspCodeAction>? Fixes,
    int Hidden,
    Action? OnToggle,
    Action<LspCodeAction>? OnApply);
