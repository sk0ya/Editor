using System.Windows.Threading;
using Editor.Core.Lsp;
using Editor.Controls.HostIntegration;

namespace Editor.Controls.Tests;

/// <summary>
/// 「どの行に電球を点けるか」の判定（<c>VimEditorControl.CodeActionBulb.cs</c>）。
/// <b>空振りする電球を出さない</b>ことと、<b>問い合わせを絞る</b>ことが要点なので、
/// 点灯結果に加えて「何回・どの範囲を聞いたか」も見る。
/// </summary>
public class CodeActionBulbProbeTests
{
    private static readonly LspCodeAction Fix = new("直す", LspCodeActionKinds.QuickFix, null);

    // 落ち着き待ち（250ms）を跨いでディスパッチャを回す。DispatcherTimer は実時間で発火するので、
    // 眠ってから汲む——眠っているだけでは Tick の継続が処理されない。
    private static void PumpThroughDebounce()
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(2);
        while (DateTime.UtcNow < deadline)
        {
            Thread.Sleep(20);
            Dispatcher.CurrentDispatcher.Invoke(static () => { }, DispatcherPriority.Background);
        }
    }

    private static void PumpUntil(Func<bool> done)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(2);
        while (DateTime.UtcNow < deadline)
        {
            Dispatcher.CurrentDispatcher.Invoke(static () => { }, DispatcherPriority.Background);
            if (done()) return;
            Thread.Sleep(20);
        }
    }

    private static EditorDiagnostic HostDiag(int line) =>
        new(EditorTextRange.Create(line, 4, line, 9), "CS0219", EditorDiagnosticSeverity.Warning);

    [Fact]
    public void Bulb_LightsOnTheCaretLine_WhenThatLineHasAFix()
    {
        LspCompletionTestHarness.WithEditor(vimEnabled: false, (editor, lsp) =>
        {
            var asked = new List<LspRange>();
            editor.HostCodeActionProvider = (range, _) =>
            {
                asked.Add(range);
                return Task.FromResult<IReadOnlyList<LspCodeAction>>([Fix]);
            };
            editor.SetText("var a = 1;\nvar b = 2;");
            editor.SetCodeActionBulbEnabled(true);
            editor.ReplaceDiagnostics([HostDiag(0)]);

            PumpUntil(() => editor.CodeActionBulbLine == 0);

            Assert.Equal(0, editor.CodeActionBulbLine);
            // 診断の範囲そのもので聞く（行頭〜行末ではなく）。
            Assert.Equal(4, Assert.Single(asked).Start.Character);
        });
    }

    [Fact]
    public void Bulb_StaysDark_WhenTheFixSourcesReturnNothing()
    {
        // 押しても「候補なし」と出るだけの電球は出さない。
        LspCompletionTestHarness.WithEditor(vimEnabled: false, (editor, lsp) =>
        {
            editor.HostCodeActionProvider = (_, _) =>
                Task.FromResult<IReadOnlyList<LspCodeAction>>([]);
            editor.SetText("var a = 1;");
            editor.SetCodeActionBulbEnabled(true);
            editor.ReplaceDiagnostics([HostDiag(0)]);

            PumpThroughDebounce();

            Assert.Equal(-1, editor.CodeActionBulbLine);
        });
    }

    [Fact]
    public void Bulb_AsksNothingForLinesWithoutADiagnostic()
    {
        // キャレットが動くたびにサーバーを叩かないための線引き。
        LspCompletionTestHarness.WithEditor(vimEnabled: false, (editor, lsp) =>
        {
            int asked = 0;
            editor.HostCodeActionProvider = (_, _) =>
            {
                asked++;
                return Task.FromResult<IReadOnlyList<LspCodeAction>>([Fix]);
            };
            editor.SetText("var a = 1;\nvar b = 2;");
            editor.SetCodeActionBulbEnabled(true);
            editor.ReplaceDiagnostics([HostDiag(1)]);   // 診断は 2 行目、キャレットは 1 行目

            PumpThroughDebounce();

            Assert.Equal(0, asked);
            Assert.Equal(-1, editor.CodeActionBulbLine);
        });
    }

    [Fact]
    public void Bulb_FollowsTheCaretToAnotherLine()
    {
        LspCompletionTestHarness.WithEditor(vimEnabled: false, (editor, lsp) =>
        {
            editor.HostCodeActionProvider = (_, _) =>
                Task.FromResult<IReadOnlyList<LspCodeAction>>([Fix]);
            editor.SetText("var a = 1;\nvar b = 2;\nvar c = 3;");
            editor.SetCodeActionBulbEnabled(true);
            editor.ReplaceDiagnostics([HostDiag(0), HostDiag(2)]);

            PumpUntil(() => editor.CodeActionBulbLine == 0);
            Assert.Equal(0, editor.CodeActionBulbLine);

            // JumpToLine は ProcessVimEvents を通らない経路（スクロールバーの印からの移動もこれ）。
            editor.JumpToLine(2, 0);
            PumpUntil(() => editor.CodeActionBulbLine == 2);
            Assert.Equal(2, editor.CodeActionBulbLine);

            // 診断の無い行へ動いたら消える。
            editor.JumpToLine(1, 0);
            PumpUntil(() => editor.CodeActionBulbLine == -1);
            Assert.Equal(-1, editor.CodeActionBulbLine);
        });
    }

    [Fact]
    public void Bulb_DisabledColumn_NeverAsks()
    {
        LspCompletionTestHarness.WithEditor(vimEnabled: false, (editor, lsp) =>
        {
            int asked = 0;
            editor.HostCodeActionProvider = (_, _) =>
            {
                asked++;
                return Task.FromResult<IReadOnlyList<LspCodeAction>>([Fix]);
            };
            editor.SetText("var a = 1;");
            editor.ReplaceDiagnostics([HostDiag(0)]);

            PumpThroughDebounce();

            Assert.Equal(0, asked);
            Assert.Equal(-1, editor.CodeActionBulbLine);
        });
    }

    [Fact]
    public void Bulb_GoesOutWhenTheColumnIsTurnedOff()
    {
        LspCompletionTestHarness.WithEditor(vimEnabled: false, (editor, lsp) =>
        {
            editor.HostCodeActionProvider = (_, _) =>
                Task.FromResult<IReadOnlyList<LspCodeAction>>([Fix]);
            editor.SetText("var a = 1;");
            editor.SetCodeActionBulbEnabled(true);
            editor.ReplaceDiagnostics([HostDiag(0)]);
            PumpUntil(() => editor.CodeActionBulbLine == 0);

            editor.SetCodeActionBulbEnabled(false);
            Assert.Equal(-1, editor.CodeActionBulbLine);
        });
    }

    [Fact]
    public void Bulb_IsReconsideredWhenTheDiagnosticsGoAway()
    {
        LspCompletionTestHarness.WithEditor(vimEnabled: false, (editor, lsp) =>
        {
            editor.HostCodeActionProvider = (_, _) =>
                Task.FromResult<IReadOnlyList<LspCodeAction>>([Fix]);
            editor.SetText("var a = 1;");
            editor.SetCodeActionBulbEnabled(true);
            editor.ReplaceDiagnostics([HostDiag(0)]);
            PumpUntil(() => editor.CodeActionBulbLine == 0);

            // 直した（診断が消えた）——同じ行に居たままでも電球は消える。
            editor.ClearDiagnostics();
            PumpUntil(() => editor.CodeActionBulbLine == -1);
            Assert.Equal(-1, editor.CodeActionBulbLine);
        });
    }
}
