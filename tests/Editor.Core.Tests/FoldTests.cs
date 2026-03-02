using Editor.Core.Config;
using Editor.Core.Engine;
using Editor.Core.Folds;
using Editor.Core.Models;
using System.Linq;

namespace Editor.Core.Tests;

// ─────────────────────────────────────────────────────────────────────────────
// FoldManager ユニットテスト
// ─────────────────────────────────────────────────────────────────────────────
public class FoldManagerTests
{
    // ── SetLspRanges ─────────────────────────────────────────────────────────

    [Fact]
    public void SetLspRanges_CreatesFoldsFromRanges()
    {
        var fm = new FoldManager();
        fm.SetLspRanges([(0, 3), (5, 8)]);

        Assert.Equal(2, fm.Folds.Count);
        Assert.Equal(0, fm.Folds[0].StartLine);
        Assert.Equal(3, fm.Folds[0].EndLine);
        Assert.Equal(5, fm.Folds[1].StartLine);
        Assert.Equal(8, fm.Folds[1].EndLine);
    }

    [Fact]
    public void SetLspRanges_FoldsAreOpenByDefault()
    {
        var fm = new FoldManager();
        fm.SetLspRanges([(0, 5)]);

        Assert.False(fm.Folds[0].IsClosed);
    }

    [Fact]
    public void SetLspRanges_FiltersInvalidRanges()
    {
        var fm = new FoldManager();
        // end <= start は無効
        fm.SetLspRanges([(3, 3), (5, 2), (0, 4)]);

        Assert.Single(fm.Folds);
        Assert.Equal(0, fm.Folds[0].StartLine);
        Assert.Equal(4, fm.Folds[0].EndLine);
    }

    [Fact]
    public void SetLspRanges_PreservesClosedStateForSameRange()
    {
        var fm = new FoldManager();
        fm.SetLspRanges([(0, 5), (10, 15)]);
        fm.CloseFold(0);   // 手動で閉じる
        fm.OpenFold(10);   // 開いたまま

        // LSP が同じ範囲を再送してきた場合
        fm.SetLspRanges([(0, 5), (10, 15)]);

        Assert.True(fm.Folds.First(f => f.StartLine == 0).IsClosed);   // 閉じた状態が保持される
        Assert.False(fm.Folds.First(f => f.StartLine == 10).IsClosed); // 開いた状態が保持される
    }

    [Fact]
    public void SetLspRanges_ReplacesOldFoldsCompletely()
    {
        var fm = new FoldManager();
        fm.SetLspRanges([(0, 5)]);
        // 範囲が変わった場合は古いフォールドが消える
        fm.SetLspRanges([(10, 20)]);

        Assert.Single(fm.Folds);
        Assert.Equal(10, fm.Folds[0].StartLine);
    }

    // ── BuildVisibleLineMap ───────────────────────────────────────────────────

    [Fact]
    public void BuildVisibleLineMap_NoFolds_AllLinesVisible()
    {
        var fm = new FoldManager();
        var map = fm.BuildVisibleLineMap(5);

        Assert.Equal([0, 1, 2, 3, 4], map);
    }

    [Fact]
    public void BuildVisibleLineMap_ClosedFold_HidesInnerLines()
    {
        var fm = new FoldManager();
        fm.SetLspRanges([(1, 3)]);
        fm.CloseFold(1);

        // 行 0, 1(開始行は表示), 4 のみ visible
        var map = fm.BuildVisibleLineMap(5);

        Assert.Equal([0, 1, 4], map);
    }

    [Fact]
    public void BuildVisibleLineMap_OpenFold_AllLinesVisible()
    {
        var fm = new FoldManager();
        fm.SetLspRanges([(1, 3)]);
        // デフォルトは open なので全行見える

        var map = fm.BuildVisibleLineMap(5);

        Assert.Equal([0, 1, 2, 3, 4], map);
    }

    [Fact]
    public void BuildVisibleLineMap_MultipleClosedFolds_SkipsCorrectLines()
    {
        var fm = new FoldManager();
        fm.SetLspRanges([(0, 2), (5, 7)]);
        fm.CloseFold(0);
        fm.CloseFold(5);

        // 可視: 0, 3, 4, 5, 8, 9
        var map = fm.BuildVisibleLineMap(10);

        Assert.Equal([0, 3, 4, 5, 8, 9], map);
    }

    [Fact]
    public void BuildVisibleLineMap_StartLineIsAlwaysVisible()
    {
        var fm = new FoldManager();
        fm.SetLspRanges([(2, 6)]);
        fm.CloseFold(2);

        var map = fm.BuildVisibleLineMap(8);

        Assert.Contains(2, map);   // 開始行は常に可視
        Assert.DoesNotContain(3, map);
        Assert.DoesNotContain(6, map);
        Assert.Contains(7, map);
    }

    // ── GetHidingFold ─────────────────────────────────────────────────────────

    [Fact]
    public void GetHidingFold_LineInsideClosedFold_ReturnsRegion()
    {
        var fm = new FoldManager();
        fm.SetLspRanges([(2, 6)]);
        fm.CloseFold(2);

        var result = fm.GetHidingFold(4);

        Assert.NotNull(result);
        Assert.Equal(2, result.Value.StartLine);
    }

    [Fact]
    public void GetHidingFold_StartLine_IsNotHidden()
    {
        var fm = new FoldManager();
        fm.SetLspRanges([(2, 6)]);
        fm.CloseFold(2);

        // StartLine 自身は隠れない
        Assert.Null(fm.GetHidingFold(2));
    }

    [Fact]
    public void GetHidingFold_OpenFold_DoesNotHide()
    {
        var fm = new FoldManager();
        fm.SetLspRanges([(2, 6)]);
        // open のまま

        Assert.Null(fm.GetHidingFold(4));
    }

    // ── Toggle / Open / Close / All ─────────────────────────────────────────

    [Fact]
    public void ToggleFold_OpenFold_ClosesFold()
    {
        var fm = new FoldManager();
        fm.SetLspRanges([(0, 3)]);

        fm.ToggleFold(0); // open → closed

        Assert.True(fm.Folds[0].IsClosed);
    }

    [Fact]
    public void ToggleFold_ClosedFold_OpensFold()
    {
        var fm = new FoldManager();
        fm.SetLspRanges([(0, 3)]);
        fm.CloseFold(0);

        fm.ToggleFold(0); // closed → open

        Assert.False(fm.Folds[0].IsClosed);
    }

    [Fact]
    public void CloseAll_ClosesAllFolds()
    {
        var fm = new FoldManager();
        fm.SetLspRanges([(0, 2), (5, 8), (10, 15)]);

        fm.CloseAll();

        Assert.All(fm.Folds, f => Assert.True(f.IsClosed));
    }

    [Fact]
    public void OpenAll_OpensAllFolds()
    {
        var fm = new FoldManager();
        fm.SetLspRanges([(0, 2), (5, 8)]);
        fm.CloseAll();

        fm.OpenAll();

        Assert.All(fm.Folds, f => Assert.False(f.IsClosed));
    }

    // ── Nested fold operations ────────────────────────────────────────────────

    [Fact]
    public void ToggleFold_NestedFolds_OnInnerStartLine_TogglesInnerNotOuter()
    {
        var fm = new FoldManager();
        // outer (0,10), inner (3,7) — cursor on line 3 (inner start)
        fm.SetLspRanges([(0, 10), (3, 7)]);

        fm.ToggleFold(3); // should close inner (3,7)

        var outer = fm.Folds.First(f => f.StartLine == 0);
        var inner = fm.Folds.First(f => f.StartLine == 3);
        Assert.False(outer.IsClosed);   // outer stays open
        Assert.True(inner.IsClosed);    // inner closed
    }

    [Fact]
    public void ToggleFold_NestedFolds_OnOuterStartLine_TogglesOuter()
    {
        var fm = new FoldManager();
        fm.SetLspRanges([(0, 10), (3, 7)]);

        fm.ToggleFold(0); // should close outer (0,10)

        var outer = fm.Folds.First(f => f.StartLine == 0);
        Assert.True(outer.IsClosed);
    }

    [Fact]
    public void ToggleFold_NestedFolds_OnInnerBodyLine_TogglesInner()
    {
        var fm = new FoldManager();
        fm.SetLspRanges([(0, 10), (3, 7)]);

        fm.ToggleFold(5); // line 5 is inside inner (3,7), should toggle inner

        var inner = fm.Folds.First(f => f.StartLine == 3);
        Assert.True(inner.IsClosed);
    }

    [Fact]
    public void ToggleFold_NestedFolds_OnOuterOnlyBodyLine_TogglesOuter()
    {
        var fm = new FoldManager();
        fm.SetLspRanges([(0, 10), (3, 7)]);

        fm.ToggleFold(9); // line 9 is inside outer but outside inner (3,7)

        var outer = fm.Folds.First(f => f.StartLine == 0);
        Assert.True(outer.IsClosed);
    }

    [Fact]
    public void BuildVisibleLineMap_NestedFolds_OuterOpenInnerClosed_HidesInnerBody()
    {
        var fm = new FoldManager();
        fm.SetLspRanges([(0, 10), (3, 7)]);
        fm.CloseFold(3); // outer open, inner closed

        var map = fm.BuildVisibleLineMap(11);

        // lines 4–7 hidden; 0,1,2,3,8,9,10 visible
        Assert.Equal([0, 1, 2, 3, 8, 9, 10], map);
    }

    [Fact]
    public void BuildVisibleLineMap_NestedFolds_OuterClosed_HidesAll()
    {
        var fm = new FoldManager();
        fm.SetLspRanges([(0, 10), (3, 7)]);
        fm.CloseFold(0); // outer closed, inner open

        var map = fm.BuildVisibleLineMap(11);

        // only line 0 visible
        Assert.Equal([0], map);
    }

    // ── CreateFold ───────────────────────────────────────────────────────────

    [Fact]
    public void CreateFold_AddsClosedFold()
    {
        var fm = new FoldManager();
        fm.CreateFold(1, 4);

        Assert.Single(fm.Folds);
        Assert.True(fm.Folds[0].IsClosed);
    }

    [Fact]
    public void CreateFold_SameLine_Ignored()
    {
        var fm = new FoldManager();
        fm.CreateFold(3, 3); // end <= start

        Assert.Empty(fm.Folds);
    }

    // ── Line adjustment on edits ─────────────────────────────────────────────

    [Fact]
    public void OnLinesDeleted_ShiftsFoldsAboveDeletion()
    {
        var fm = new FoldManager();
        fm.SetLspRanges([(10, 15)]);

        fm.OnLinesDeleted(0, 4); // 行 0–4 を削除

        Assert.Equal(5, fm.Folds[0].StartLine);
        Assert.Equal(10, fm.Folds[0].EndLine);
    }

    [Fact]
    public void OnLinesInserted_ShiftsFoldsAfterInsertPoint()
    {
        var fm = new FoldManager();
        fm.SetLspRanges([(5, 10)]);

        fm.OnLinesInserted(2, 3); // 行 2 の後に 3 行挿入

        Assert.Equal(8, fm.Folds[0].StartLine);
        Assert.Equal(13, fm.Folds[0].EndLine);
    }

    [Fact]
    public void OnLinesDeleted_RemovesFoldContainedInDeletedRange()
    {
        var fm = new FoldManager();
        fm.SetLspRanges([(3, 6)]);

        fm.OnLinesDeleted(2, 8);

        Assert.Empty(fm.Folds);
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// VimEngine フォールド統合テスト
// ─────────────────────────────────────────────────────────────────────────────
public class VimEngineFoldTests
{
    private static VimEngine CreateEngine(string text = "")
    {
        var engine = new VimEngine(new VimConfig());
        if (!string.IsNullOrEmpty(text))
            engine.SetText(text);
        return engine;
    }

    // 10行のサンプルテキスト (行0–9)
    private static string TenLines() =>
        string.Join("\n", Enumerable.Range(0, 10).Select(i => $"line{i}"));

    // ── LoadFoldRanges ────────────────────────────────────────────────────────

    [Fact]
    public void LoadFoldRanges_AppliesFoldsToCurrentBuffer()
    {
        var engine = CreateEngine(TenLines());

        engine.LoadFoldRanges([(0, 3), (5, 8)]);

        Assert.Equal(2, engine.CurrentBuffer.Folds.Folds.Count);
    }

    [Fact]
    public void LoadFoldRanges_FoldsAreOpenByDefault()
    {
        var engine = CreateEngine(TenLines());

        engine.LoadFoldRanges([(0, 5)]);

        Assert.False(engine.CurrentBuffer.Folds.Folds[0].IsClosed);
    }

    [Fact]
    public void LoadFoldRanges_CalledTwice_PreservesUserToggledState()
    {
        var engine = CreateEngine(TenLines());
        engine.LoadFoldRanges([(0, 3)]);

        // ユーザーが手動で閉じた
        engine.CurrentBuffer.Folds.CloseFold(0);
        Assert.True(engine.CurrentBuffer.Folds.Folds[0].IsClosed);

        // LSP が同じ範囲を再送
        engine.LoadFoldRanges([(0, 3)]);

        // 閉じた状態が維持されている
        Assert.True(engine.CurrentBuffer.Folds.Folds[0].IsClosed);
    }

    // ── za / zc / zo / zM / zR ───────────────────────────────────────────────

    [Fact]
    public void Za_TogglesOpenFoldClosed()
    {
        var engine = CreateEngine(TenLines());
        engine.LoadFoldRanges([(0, 4)]);

        var events = engine.ProcessKey("za");

        Assert.True(engine.CurrentBuffer.Folds.Folds[0].IsClosed);
        Assert.Contains(events, e => e.Type == VimEventType.FoldsChanged);
    }

    [Fact]
    public void Za_TogglesClosedFoldOpen()
    {
        var engine = CreateEngine(TenLines());
        engine.LoadFoldRanges([(0, 4)]);
        engine.CurrentBuffer.Folds.CloseFold(0);

        engine.ProcessKey("za");

        Assert.False(engine.CurrentBuffer.Folds.Folds[0].IsClosed);
    }

    [Fact]
    public void Zc_ClosesFoldAtCursor()
    {
        var engine = CreateEngine(TenLines());
        engine.LoadFoldRanges([(0, 4)]);

        var events = engine.ProcessKey("zc");

        Assert.True(engine.CurrentBuffer.Folds.Folds[0].IsClosed);
        Assert.Contains(events, e => e.Type == VimEventType.FoldsChanged);
    }

    [Fact]
    public void Zo_OpensFoldAtCursor()
    {
        var engine = CreateEngine(TenLines());
        engine.LoadFoldRanges([(0, 4)]);
        engine.CurrentBuffer.Folds.CloseFold(0);

        var events = engine.ProcessKey("zo");

        Assert.False(engine.CurrentBuffer.Folds.Folds[0].IsClosed);
        Assert.Contains(events, e => e.Type == VimEventType.FoldsChanged);
    }

    [Fact]
    public void ZM_ClosesAllFolds()
    {
        var engine = CreateEngine(TenLines());
        engine.LoadFoldRanges([(0, 2), (5, 7)]);

        engine.ProcessKey("z");
        var events = engine.ProcessKey("M");

        Assert.All(engine.CurrentBuffer.Folds.Folds, f => Assert.True(f.IsClosed));
        Assert.Contains(events, e => e.Type == VimEventType.FoldsChanged);
    }

    [Fact]
    public void ZR_OpensAllFolds()
    {
        var engine = CreateEngine(TenLines());
        engine.LoadFoldRanges([(0, 2), (5, 7)]);
        engine.CurrentBuffer.Folds.CloseAll();

        engine.ProcessKey("z");
        var events = engine.ProcessKey("R");

        Assert.All(engine.CurrentBuffer.Folds.Folds, f => Assert.False(f.IsClosed));
        Assert.Contains(events, e => e.Type == VimEventType.FoldsChanged);
    }

    [Fact]
    public void Zf_CreatesFoldForCount()
    {
        var engine = CreateEngine(TenLines());
        // カーソルを行 2 に移動
        engine.ProcessKey("j");
        engine.ProcessKey("j");

        // 3zf → 行2–4 をフォールド
        engine.ProcessKey("3");
        engine.ProcessKey("z");
        var events = engine.ProcessKey("f");

        Assert.Single(engine.CurrentBuffer.Folds.Folds);
        Assert.Equal(2, engine.CurrentBuffer.Folds.Folds[0].StartLine);
        Assert.Equal(4, engine.CurrentBuffer.Folds.Folds[0].EndLine);
        Assert.Contains(events, e => e.Type == VimEventType.FoldsChanged);
    }

    // ── fold-aware cursor movement ────────────────────────────────────────────

    [Fact]
    public void J_WithClosedFold_SkipsHiddenLines()
    {
        var engine = CreateEngine(TenLines());
        engine.LoadFoldRanges([(1, 3)]); // 行 1–3 をフォールド
        engine.CurrentBuffer.Folds.CloseFold(1);

        // カーソルは行 0 → j → 行 1(フォールド開始) → j → 行 4(フォールドの次)
        engine.ProcessKey("j");
        Assert.Equal(1, engine.Cursor.Line);

        engine.ProcessKey("j");
        // 行 2, 3 は隠れているので行 4 へジャンプ
        Assert.Equal(4, engine.Cursor.Line);
    }

    [Fact]
    public void K_WithClosedFold_SkipsHiddenLines()
    {
        var engine = CreateEngine(TenLines());
        engine.LoadFoldRanges([(1, 3)]);
        engine.CurrentBuffer.Folds.CloseFold(1);

        // 可視行マップ: [0, 1, 4, 5, 6, 7, 8, 9]
        // j×2 → vis 0→1→2 → 行 4
        engine.ProcessKey("j");
        engine.ProcessKey("j");
        Assert.Equal(4, engine.Cursor.Line);

        // k → vis 2→1 → 行 1 (フォールド開始行、行 2, 3 はスキップ)
        engine.ProcessKey("k");
        Assert.Equal(1, engine.Cursor.Line);
    }

    [Fact]
    public void Zc_CursorInsideFold_MovesToStartLine()
    {
        // カーソルがフォールド内にいるときに閉じると開始行に移動する
        var engine = CreateEngine(TenLines());
        engine.LoadFoldRanges([(0, 5)]);

        // 行 3 に移動
        engine.ProcessKey("j");
        engine.ProcessKey("j");
        engine.ProcessKey("j");
        Assert.Equal(3, engine.Cursor.Line);

        // フォールドを閉じる (zM で全部閉じる)
        engine.ProcessKey("z");
        engine.ProcessKey("M");

        // カーソルはフォールド開始行に移動するはず
        Assert.Equal(0, engine.Cursor.Line);
    }

    [Fact]
    public void J_WithOpenFold_MovesNormally()
    {
        // open なフォールドは通常の移動
        var engine = CreateEngine(TenLines());
        engine.LoadFoldRanges([(1, 3)]);
        // CloseFold は呼ばない

        engine.ProcessKey("j");
        Assert.Equal(1, engine.Cursor.Line);
        engine.ProcessKey("j");
        Assert.Equal(2, engine.Cursor.Line); // 隠れていないので普通に進む
    }

    // ── undo clears folds ─────────────────────────────────────────────────────

    [Fact]
    public void Undo_ClearsFolds()
    {
        var engine = CreateEngine(TenLines());
        engine.LoadFoldRanges([(0, 3)]);
        engine.CurrentBuffer.Folds.CloseFold(0);

        // テキストを変更してアンドゥ
        engine.ProcessKey("i");
        engine.ProcessKey("x");
        engine.ProcessKey("Escape");
        engine.ProcessKey("u");

        Assert.Empty(engine.CurrentBuffer.Folds.Folds);
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// SyntaxFoldDetector テスト（LSP 非対応時のフォールバック）
// ─────────────────────────────────────────────────────────────────────────────
public class SyntaxFoldDetectorTests
{
    // ── C# brace matching ────────────────────────────────────────────────────

    [Fact]
    public void CSharp_SimpleClass_DetectsFolds()
    {
        var lines = new[]
        {
            "public class Foo",   // 0
            "{",                  // 1
            "    void Bar()",     // 2
            "    {",              // 3
            "        return;",    // 4
            "    }",              // 5
            "}",                  // 6
        };

        var folds = SyntaxFoldDetector.Detect(".cs", lines);

        // { } で囲まれたブロックが検出される
        Assert.Contains(folds, f => f.StartLine == 1 && f.EndLine == 6); // class body
        Assert.Contains(folds, f => f.StartLine == 3 && f.EndLine == 5); // method body
    }

    [Fact]
    public void CSharp_SingleLineBrace_NotDetected()
    {
        // 同一行で開閉する場合はフォールド不要
        var lines = new[]
        {
            "var x = new[] { 1, 2, 3 };",
        };

        var folds = SyntaxFoldDetector.Detect(".cs", lines);

        Assert.Empty(folds);
    }

    [Fact]
    public void CSharp_BraceInString_NotCounted()
    {
        var lines = new[]
        {
            "class A",        // 0
            "{",              // 1
            "    var s = \"{\";", // 2  ← 文字列内の { はカウントしない
            "}",              // 3
        };

        var folds = SyntaxFoldDetector.Detect(".cs", lines);

        // class body のみ検出（文字列内 { は無視）
        Assert.Single(folds);
        Assert.Equal(1, folds[0].StartLine);
        Assert.Equal(3, folds[0].EndLine);
    }

    [Fact]
    public void CSharp_LineComment_BraceIgnored()
    {
        var lines = new[]
        {
            "class A",       // 0
            "{",             // 1
            "    // {",      // 2  ← コメント内の { は無視
            "}",             // 3
        };

        var folds = SyntaxFoldDetector.Detect(".cs", lines);

        Assert.Single(folds);
    }

    [Fact]
    public void CSharp_BlockComment_BraceIgnored()
    {
        var lines = new[]
        {
            "class A",        // 0
            "{",              // 1
            "    /* { */",    // 2  ← ブロックコメント内の { は無視
            "}",              // 3
        };

        var folds = SyntaxFoldDetector.Detect(".cs", lines);

        Assert.Single(folds);
    }

    // ── #region / #endregion ────────────────────────────────────────────────

    [Fact]
    public void CSharp_Region_Detected()
    {
        var lines = new[]
        {
            "class A",           // 0
            "{",                 // 1
            "#region Helpers",   // 2
            "    void Foo() {}",  // 3
            "#endregion",        // 4
            "}",                 // 5
        };

        var folds = SyntaxFoldDetector.Detect(".cs", lines);

        Assert.Contains(folds, f => f.StartLine == 2 && f.EndLine == 4);
    }

    [Fact]
    public void CSharp_NestedRegions_BothDetected()
    {
        var lines = new[]
        {
            "#region Outer",    // 0
            "#region Inner",    // 1
            "code",             // 2
            "#endregion",       // 3
            "#endregion",       // 4
        };

        var folds = SyntaxFoldDetector.Detect(".cs", lines);

        Assert.Contains(folds, f => f.StartLine == 0 && f.EndLine == 4);
        Assert.Contains(folds, f => f.StartLine == 1 && f.EndLine == 3);
    }

    // ── Non-CS extensions ─────────────────────────────────────────────────

    [Fact]
    public void JavaScript_BraceFolds_Detected()
    {
        var lines = new[]
        {
            "function foo() {",   // 0
            "    if (x) {",       // 1
            "        return;",    // 2
            "    }",              // 3
            "}",                  // 4
        };

        var folds = SyntaxFoldDetector.Detect(".js", lines);

        Assert.Contains(folds, f => f.StartLine == 0 && f.EndLine == 4);
        Assert.Contains(folds, f => f.StartLine == 1 && f.EndLine == 3);
    }

    [Fact]
    public void UnsupportedExtension_ReturnsEmpty()
    {
        var lines = new[] { "hello world", "nothing here" };

        var folds = SyntaxFoldDetector.Detect(".txt", lines);

        Assert.Empty(folds);
    }

    // ── Rust ─────────────────────────────────────────────────────────────────

    [Fact]
    public void Rust_FnBlock_Detected()
    {
        var lines = new[]
        {
            "fn main() {",      // 0
            "    let x = 1;",   // 1
            "}",                // 2
        };

        var folds = SyntaxFoldDetector.Detect(".rs", lines);

        Assert.Contains(folds, f => f.StartLine == 0 && f.EndLine == 2);
    }

    [Fact]
    public void Rust_LineComment_BraceIgnored()
    {
        var lines = new[]
        {
            "fn foo() {",       // 0
            "    // let x = {", // 1  ← コメント内の { は無視
            "}",                // 2
        };

        var folds = SyntaxFoldDetector.Detect(".rs", lines);

        Assert.Single(folds);
    }

    // ── Markdown ─────────────────────────────────────────────────────────────

    [Fact]
    public void Markdown_H1Section_FoldsToNextH1()
    {
        var lines = new[]
        {
            "# Chapter 1",     // 0
            "content",         // 1
            "more content",    // 2
            "# Chapter 2",     // 3
            "other",           // 4
        };

        var folds = SyntaxFoldDetector.Detect(".md", lines);

        Assert.Contains(folds, f => f.StartLine == 0 && f.EndLine == 2);
        Assert.Contains(folds, f => f.StartLine == 3 && f.EndLine == 4);
    }

    [Fact]
    public void Markdown_H2SubSection_FoldsIndependently()
    {
        var lines = new[]
        {
            "# Top",        // 0
            "## Sub",       // 1
            "content",      // 2
            "## Sub2",      // 3
            "content",      // 4
        };

        var folds = SyntaxFoldDetector.Detect(".md", lines);

        // # Top は行4まで折りたたむ
        Assert.Contains(folds, f => f.StartLine == 0 && f.EndLine == 4);
        // ## Sub は行2まで
        Assert.Contains(folds, f => f.StartLine == 1 && f.EndLine == 2);
        // ## Sub2 は行4まで
        Assert.Contains(folds, f => f.StartLine == 3 && f.EndLine == 4);
    }

    [Fact]
    public void Markdown_CodeBlock_Detected()
    {
        var lines = new[]
        {
            "text",      // 0
            "```csharp", // 1
            "var x = 1;",// 2
            "```",       // 3
            "more text", // 4
        };

        var folds = SyntaxFoldDetector.Detect(".md", lines);

        Assert.Contains(folds, f => f.StartLine == 1 && f.EndLine == 3);
    }

    [Fact]
    public void Markdown_TrailingBlankLines_TrimmedFromFold()
    {
        var lines = new[]
        {
            "# Heading",   // 0
            "content",     // 1
            "",            // 2  ← 末尾の空行
            "# Next",      // 3
        };

        var folds = SyntaxFoldDetector.Detect(".md", lines);

        // 末尾空行を詰めて EndLine == 1 になる
        Assert.Contains(folds, f => f.StartLine == 0 && f.EndLine == 1);
    }

    // ── XML / XAML ────────────────────────────────────────────────────────────

    [Fact]
    public void Xml_NestedElements_BothDetected()
    {
        var lines = new[]
        {
            "<root>",           // 0
            "  <child>",        // 1
            "    <value/>",     // 2  自己閉じ
            "  </child>",       // 3
            "</root>",          // 4
        };

        var folds = SyntaxFoldDetector.Detect(".xml", lines);

        Assert.Contains(folds, f => f.StartLine == 0 && f.EndLine == 4);
        Assert.Contains(folds, f => f.StartLine == 1 && f.EndLine == 3);
    }

    [Fact]
    public void Xml_SelfClosingTag_NoFold()
    {
        var lines = new[]
        {
            "<root>",       // 0
            "  <item />",   // 1  自己閉じ → フォールド生成しない
            "</root>",      // 2
        };

        var folds = SyntaxFoldDetector.Detect(".xml", lines);

        Assert.DoesNotContain(folds, f => f.StartLine == 1);
        Assert.Contains(folds, f => f.StartLine == 0 && f.EndLine == 2);
    }

    [Fact]
    public void Xml_Comment_Skipped()
    {
        var lines = new[]
        {
            "<root>",              // 0
            "<!-- <fake> -->",     // 1  コメント内タグは無視
            "</root>",             // 2
        };

        var folds = SyntaxFoldDetector.Detect(".xml", lines);

        Assert.Single(folds);
        Assert.Equal(0, folds[0].StartLine);
        Assert.Equal(2, folds[0].EndLine);
    }

    [Fact]
    public void Xaml_NestedControls_Detected()
    {
        var lines = new[]
        {
            "<Grid>",                   // 0
            "  <StackPanel>",            // 1
            "    <Button Content=\"X\"/>",// 2
            "  </StackPanel>",           // 3
            "</Grid>",                   // 4
        };

        var folds = SyntaxFoldDetector.Detect(".xaml", lines);

        Assert.Contains(folds, f => f.StartLine == 0 && f.EndLine == 4);
        Assert.Contains(folds, f => f.StartLine == 1 && f.EndLine == 3);
    }

    // ── JSON ─────────────────────────────────────────────────────────────────

    [Fact]
    public void Json_NestedObject_Detected()
    {
        var lines = new[]
        {
            "{",            // 0
            "  \"name\": \"foo\",", // 1
            "  \"inner\": {",      // 2
            "    \"x\": 1",        // 3
            "  }",          // 4
            "}",            // 5
        };

        var folds = SyntaxFoldDetector.Detect(".json", lines);

        Assert.Contains(folds, f => f.StartLine == 0 && f.EndLine == 5);
        Assert.Contains(folds, f => f.StartLine == 2 && f.EndLine == 4);
    }

    [Fact]
    public void Json_Array_Detected()
    {
        var lines = new[]
        {
            "[",            // 0
            "  1,",         // 1
            "  2,",         // 2
            "  3",          // 3
            "]",            // 4
        };

        var folds = SyntaxFoldDetector.Detect(".json", lines);

        Assert.Contains(folds, f => f.StartLine == 0 && f.EndLine == 4);
    }

    [Fact]
    public void Json_BraceInString_NotCounted()
    {
        var lines = new[]
        {
            "{",                    // 0
            "  \"key\": \"val{x}\",",// 1  ← 文字列内の { は無視
            "}",                    // 2
        };

        var folds = SyntaxFoldDetector.Detect(".json", lines);

        Assert.Single(folds);
    }

    [Fact]
    public void Jsonc_LineComment_BraceIgnored()
    {
        var lines = new[]
        {
            "{",            // 0
            "  // {",       // 1  ← JSONC コメント内 { は無視
            "}",            // 2
        };

        var folds = SyntaxFoldDetector.Detect(".jsonc", lines);

        Assert.Single(folds);
    }

    // ── Python indentation ───────────────────────────────────────────────────

    [Fact]
    public void Python_DefBlock_Detected()
    {
        var lines = new[]
        {
            "def foo():",        // 0
            "    x = 1",         // 1
            "    return x",      // 2
            "",                  // 3
            "def bar():",        // 4
            "    pass",          // 5
        };

        var folds = SyntaxFoldDetector.Detect(".py", lines);

        Assert.Contains(folds, f => f.StartLine == 0);
        Assert.Contains(folds, f => f.StartLine == 4);
    }

    [Fact]
    public void Python_ClassBlock_Detected()
    {
        var lines = new[]
        {
            "class Foo:",        // 0
            "    def __init__(self):", // 1
            "        pass",      // 2
        };

        var folds = SyntaxFoldDetector.Detect(".py", lines);

        Assert.Contains(folds, f => f.StartLine == 0);
        Assert.Contains(folds, f => f.StartLine == 1);
    }

    // ── JS / TS ───────────────────────────────────────────────────────────────

    [Fact]
    public void TypeScript_Class_NestedFoldsDetected()
    {
        var lines = new[]
        {
            "class Foo {",          // 0
            "    constructor() {",  // 1
            "        this.x = 1;",  // 2
            "    }",                // 3
            "    method() {",       // 4
            "        return 1;",    // 5
            "    }",                // 6
            "}",                    // 7
        };

        var folds = SyntaxFoldDetector.Detect(".ts", lines);

        Assert.Contains(folds, f => f.StartLine == 0 && f.EndLine == 7); // class
        Assert.Contains(folds, f => f.StartLine == 1 && f.EndLine == 3); // constructor
        Assert.Contains(folds, f => f.StartLine == 4 && f.EndLine == 6); // method
    }

    [Fact]
    public void JavaScript_ArrowFunction_Detected()
    {
        var lines = new[]
        {
            "const foo = () => {",  // 0
            "    return {",         // 1
            "        a: 1",         // 2
            "    };",               // 3
            "};",                   // 4
        };

        var folds = SyntaxFoldDetector.Detect(".js", lines);

        Assert.Contains(folds, f => f.StartLine == 1 && f.EndLine == 3); // inner object
        Assert.Contains(folds, f => f.StartLine == 0 && f.EndLine == 4); // outer function
    }

    [Fact]
    public void JavaScript_TemplateLiteralSingleLine_NoSpuriousFold()
    {
        // ${...} が同一行に収まる場合は誤検出しない
        var lines = new[]
        {
            "function greet(name) {",          // 0
            "    return `Hello ${name}!`;",    // 1
            "}",                               // 2
        };

        var folds = SyntaxFoldDetector.Detect(".js", lines);

        // function body のみ
        Assert.Single(folds);
        Assert.Equal(0, folds[0].StartLine);
        Assert.Equal(2, folds[0].EndLine);
    }

    [Fact]
    public void JavaScript_UnknownExtension_ReturnsEmpty()
    {
        var lines = new[] { "function foo() {", "}" };
        var folds = SyntaxFoldDetector.Detect(".unknown", lines);
        Assert.Empty(folds);
    }
}
