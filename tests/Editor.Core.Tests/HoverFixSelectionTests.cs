using Editor.Core.Lsp;

namespace Editor.Core.Tests;

public class HoverFixSelectionTests
{
    /// <summary>サーバーは <c>only: ["quickfix"]</c> を守らないことがある（実測でリファクタリングまで
    /// 返ってきた）。電球の中身は「直す」ものだけにする。</summary>
    [Fact]
    public void Take_DropsRefactorings()
    {
        var quickFix = Action("未使用の変数を削除する", LspCodeActionKinds.QuickFix);
        var fixAll = Action("すべて修正: 未使用の変数を削除する", LspCodeActionKinds.SourceFixAll);
        var extract = Action("メソッドを抽出する", LspCodeActionKinds.RefactorExtract);
        var rewrite = Action("暗黙的な型の使用", LspCodeActionKinds.RefactorRewrite);

        var (shown, hidden) = HoverFixSelection.Take([quickFix, extract, fixAll, rewrite]);

        Assert.Equal([quickFix, fixAll], shown);
        Assert.Equal(0, hidden);
    }

    /// <summary>種別を名乗らないサーバーの分は落とさない——落とすと「修正が無い」ように見える。</summary>
    [Fact]
    public void Take_KeepsActionsWithoutKind()
    {
        var unknown = Action("直す", null);

        var (shown, _) = HoverFixSelection.Take([unknown]);

        Assert.Equal([unknown], shown);
    }

    [Fact]
    public void Take_PutsPreferredFirstKeepingTheRestInOrder()
    {
        var first = Action("1", LspCodeActionKinds.QuickFix);
        var second = Action("2", LspCodeActionKinds.QuickFix);
        var preferred = Action("推奨", LspCodeActionKinds.QuickFix, preferred: true);

        var (shown, _) = HoverFixSelection.Take([first, second, preferred]);

        Assert.Equal([preferred, first, second], shown);
    }

    [Fact]
    public void Take_CapsTheListAndReportsTheRest()
    {
        var actions = Enumerable.Range(0, 11)
            .Select(i => Action($"fix {i}", LspCodeActionKinds.QuickFix))
            .ToArray();

        var (shown, hidden) = HoverFixSelection.Take(actions, max: 4);

        Assert.Equal(4, shown.Count);
        Assert.Equal(7, hidden);
    }

    [Fact]
    public void Take_WithNothingToShow_IsEmpty()
    {
        Assert.Empty(HoverFixSelection.Take([]).Shown);
        var refactorOnly = HoverFixSelection.Take([Action("抽出", LspCodeActionKinds.Refactor)]);
        Assert.Empty(refactorOnly.Shown);
        Assert.Equal(0, refactorOnly.Hidden);
    }

    private static LspCodeAction Action(string title, string? kind, bool preferred = false) =>
        new(title, kind, null, IsPreferred: preferred);
}
