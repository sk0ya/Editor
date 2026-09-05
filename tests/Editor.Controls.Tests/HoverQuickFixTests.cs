using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Editor.Controls.Rendering;
using Editor.Controls.Themes;
using Editor.Core.Lsp;
using Editor.Core.Text;

namespace Editor.Controls.Tests;

/// <summary>ホバーの説明ポップアップの電球から、警告をその場で直せること。</summary>
public class HoverQuickFixTests
{
    [Fact]
    public void FixRow_Click_InvokesApplyWithThatAction()
    {
        WpfTestHost.Run(() =>
        {
            var fix = new LspCodeAction("using を削除", LspCodeActionKinds.QuickFix, null);
            LspCodeAction? applied = null;

            var content = HoverContentBuilder.Build(
                [Warning("CS8019: 不要な using")], HoverMarkdown.Parse("説明"),
                EditorTheme.Dracula, new FontFamily("Consolas"), 12, null,
                new HoverFixSection(
                    Show: true, Expanded: true, Loading: false, Loaded: true,
                    Fixes: [fix], Hidden: 0, OnToggle: null, OnApply: action => applied = action));

            var row = FindByTag(content, fix);
            Assert.NotNull(row);
            Click(row!);

            Assert.Same(fix, applied);
        });
    }

    [Fact]
    public void Hover_WithNoDiagnostic_ShowsNoBulbAndAsksNothing()
    {
        LspCompletionTestHarness.WithEditor(vimEnabled: false, (editor, lsp) =>
        {
            lsp.HoverResult = "```csharp\nint Count\n```";
            lsp.CodeActions.Add(new LspCodeAction("直す", LspCodeActionKinds.QuickFix, null));
            editor.SetText("var count = 1;");

            var hover = editor.ShowHoverInfoForTestAsync(0, 4);
            LspCompletionTestHarness.Pump();

            Assert.True(hover.IsCompleted);
            var content = editor.HoverPopupContentForTest;
            Assert.NotNull(content);                            // 説明そのものは出ている
            Assert.Null(FindByTag(content!, HoverContentBuilder.FixToggleTag));
            Assert.Empty(lsp.CodeActionRequests);               // 診断が無いので修正は聞かない
        });
    }

    /// <summary>ホバーしただけでは<b>問い合わせない</b>。マウスを走らせるだけで診断の数だけ
    /// Roslyn／サーバーの計算を積み上げないための線引き（電球を押して初めて聞く）。</summary>
    [Fact]
    public void Hover_OnDiagnostic_DoesNotAskForFixesUntilTheBulbIsPressed()
    {
        LspCompletionTestHarness.WithEditor(vimEnabled: false, (editor, lsp) =>
        {
            lsp.HoverResult = "```csharp\nint count\n```";
            lsp.CodeActions.Add(new LspCodeAction("変数を削除", LspCodeActionKinds.QuickFix, null));
            editor.SetText("var count = 1;");
            editor.Canvas.SetDiagnostics([Warning("CS0219", 0, 4, 9)]);

            _ = editor.ShowHoverInfoForTestAsync(0, 6);
            LspCompletionTestHarness.Pump();

            Assert.NotNull(FindByTag(editor.HoverPopupContentForTest!, HoverContentBuilder.FixToggleTag));
            Assert.Empty(lsp.CodeActionRequests);
        });
    }

    [Fact]
    public void Hover_OnDiagnostic_ShowsFixesForThatDiagnosticRange()
    {
        LspCompletionTestHarness.WithEditor(vimEnabled: false, (editor, lsp) =>
        {
            var diagnostic = Warning("CS0219: 値が使われていません", 0, 4, 9);
            var fix = new LspCodeAction("変数を削除", LspCodeActionKinds.QuickFix, null);
            lsp.HoverResult = "```csharp\nint count\n```";
            lsp.CodeActions.Add(fix);
            editor.SetText("var count = 1;");
            editor.Canvas.SetDiagnostics([diagnostic]);

            var hover = editor.ShowHoverInfoForTestAsync(0, 6);
            LspCompletionTestHarness.Pump();

            Assert.True(hover.IsCompleted);
            // 最初は電球だけ。候補は積み上げないし、問い合わせもしない。
            var content = editor.HoverPopupContentForTest!;
            Assert.NotNull(FindByTag(content, HoverContentBuilder.FixToggleTag));
            Assert.Null(FindByTag(content, fix));

            Click(FindByTag(content, HoverContentBuilder.FixToggleTag)!);
            LspCompletionTestHarness.Pump();

            var request = Assert.Single(lsp.CodeActionRequests);
            Assert.Equal(diagnostic.Range, request.Range);
            Assert.Equal(new[] { LspCodeActionKinds.QuickFix }, request.Only);
            Assert.NotNull(FindByTag(editor.HoverPopupContentForTest!, fix));
        });
    }

    /// <summary>本命：ポップアップの修正行を押すと、その編集がバッファへ入る。</summary>
    [Fact]
    public void Hover_ClickingFix_AppliesTheEditToTheBuffer()
    {
        var path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), $"hover-fix-{Guid.NewGuid():N}.cs");
        System.IO.File.WriteAllText(path, "var count = 1;\n");
        try
        {
            LspCompletionTestHarness.WithEditor(vimEnabled: false, (editor, lsp) =>
            {
                editor.LoadFile(path);
                var edit = new LspWorkspaceEdit(new Dictionary<string, IReadOnlyList<LspTextEdit>>
                {
                    [LspUri.FromPath(path)] =
                    [
                        new LspTextEdit(
                            new LspRange(new LspPosition(0, 0), new LspPosition(0, 14)), "// 削除済み"),
                    ],
                });
                var fix = new LspCodeAction("変数を削除", LspCodeActionKinds.QuickFix, edit);
                lsp.HoverResult = "```csharp\nint count\n```";
                lsp.CodeActions.Add(fix);
                editor.Canvas.SetDiagnostics([Warning("CS0219", 0, 4, 9)]);

                _ = editor.ShowHoverInfoForTestAsync(0, 6);
                LspCompletionTestHarness.Pump();

                Click(FindByTag(editor.HoverPopupContentForTest!, HoverContentBuilder.FixToggleTag)!);
                LspCompletionTestHarness.Pump();
                var row = FindByTag(editor.HoverPopupContentForTest!, fix);
                Assert.NotNull(row);

                Click(row!);
                LspCompletionTestHarness.Pump();

                Assert.StartsWith("// 削除済み", editor.Text);
                Assert.Null(editor.HoverPopupContentForTest);   // 押したら閉じる
            });
        }
        finally
        {
            try { System.IO.File.Delete(path); } catch { }
        }
    }

    /// <summary>ホストが編集プレビューなどで取り消したとき——本文は変えず、<b>失敗とは言わない</b>
    /// （自分で止めた操作をエラーとして突き返さない）。</summary>
    [Fact]
    public void Hover_FixCancelledByHost_LeavesTheBufferAloneAndReportsCancelled()
    {
        var path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), $"hover-cancel-{Guid.NewGuid():N}.cs");
        System.IO.File.WriteAllText(path, "var count = 1;\n");
        try
        {
            LspCompletionTestHarness.WithEditor(vimEnabled: false, (editor, lsp) =>
            {
                editor.LoadFile(path);
                editor.WorkspaceEditRequested += (_, e) => e.Cancelled = true;
                var edit = new LspWorkspaceEdit(new Dictionary<string, IReadOnlyList<LspTextEdit>>
                {
                    [LspUri.FromPath(path)] =
                    [
                        new LspTextEdit(
                            new LspRange(new LspPosition(0, 0), new LspPosition(0, 14)), "// 削除済み"),
                    ],
                });
                var fix = new LspCodeAction("変数を削除", LspCodeActionKinds.QuickFix, edit);
                lsp.HoverResult = "```csharp\nint count\n```";
                lsp.CodeActions.Add(fix);
                editor.Canvas.SetDiagnostics([Warning("CS0219", 0, 4, 9)]);

                _ = editor.ShowHoverInfoForTestAsync(0, 6);
                LspCompletionTestHarness.Pump();
                Click(FindByTag(editor.HoverPopupContentForTest!, HoverContentBuilder.FixToggleTag)!);
                LspCompletionTestHarness.Pump();
                Click(FindByTag(editor.HoverPopupContentForTest!, fix)!);
                LspCompletionTestHarness.Pump();

                Assert.StartsWith("var count = 1;", editor.Text);
                Assert.Contains("cancelled", editor.CurrentStatusText);
                Assert.DoesNotContain("failed", editor.CurrentStatusText);
            });
        }
        finally
        {
            try { System.IO.File.Delete(path); } catch { }
        }
    }

    private static LspDiagnostic Warning(string message, int line = 0, int start = 0, int end = 1) =>
        new(new LspRange(new LspPosition(line, start), new LspPosition(line, end)),
            message, DiagnosticSeverity.Warning);

    /// <summary>その目印（<see cref="FrameworkElement.Tag"/>）を載せた行。修正行はアクション自身、
    /// 電球は <see cref="HoverContentBuilder.FixToggleTag"/> を載せている。</summary>
    private static FrameworkElement? FindByTag(DependencyObject root, object tag)
    {
        if (root is FrameworkElement element && Equals(element.Tag, tag)) return element;

        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            if (FindByTag(VisualTreeHelper.GetChild(root, i), tag) is { } found) return found;
        }

        // 未レイアウトの要素はビジュアルツリーに現れないことがあるので、論理側もたどる。
        if (root is Panel panel)
        {
            foreach (var child in panel.Children)
            {
                if (child is DependencyObject dependency && FindByTag(dependency, tag) is { } found)
                    return found;
            }
        }
        if (root is Border { Child: DependencyObject borderChild })
            return FindByTag(borderChild, tag);
        if (root is ContentControl { Content: DependencyObject content })
            return FindByTag(content, tag);
        return null;
    }

    private static void Click(UIElement target) =>
        target.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left)
        {
            RoutedEvent = UIElement.MouseLeftButtonUpEvent,
        });
}
