using Editor.Core.Config;
using Editor.Core.Engine;
using Editor.Core.Models;
using System.Linq;

namespace Editor.Core.Tests;

/// <summary>
/// 飛んだ先が閉じた折りたたみの中なら、そこを開く（Zed 1.25 の「go to line で折りたたみを開く」、Vim の 'foldopen' に相当）。
/// ホストからの移動（定義へ・軌跡・Problems・アウトライン＝<see cref="VimEngine.SetCursorPosition"/>）と、
/// ジャンプリストに積む移動（G・gg・検索・%・Ctrl+O 等）、<c>:行番号</c> が対象。j/k は折りたたみを飛び越えるだけで開かない。
/// </summary>
public class FoldRevealTests
{
    private static VimEngine CreateEngine()
    {
        var engine = new VimEngine(new VimConfig());
        engine.SetText(string.Join("\n", Enumerable.Range(0, 12).Select(i => $"line{i}")));
        engine.LoadFoldRanges([(2, 6)]);
        engine.CurrentBuffer.Folds.CloseAll();
        return engine;
    }

    private static bool IsOpen(VimEngine engine) => !engine.CurrentBuffer.Folds.Folds.Single().IsClosed;

    [Fact]
    public void Host_navigation_into_a_closed_fold_opens_it()
    {
        var engine = CreateEngine();
        var events = engine.SetCursorPosition(new CursorPosition(4, 0));

        Assert.True(IsOpen(engine));
        Assert.Equal(4, engine.Cursor.Line);
        Assert.Contains(events, e => e.Type == VimEventType.FoldsChanged);
    }

    [Fact]
    public void Landing_on_the_fold_header_keeps_it_closed()
    {
        var engine = CreateEngine();
        engine.SetCursorPosition(new CursorPosition(2, 0));   // 見出しの行は隠れていない
        Assert.False(IsOpen(engine));
    }

    [Fact]
    public void Host_selection_into_a_closed_fold_opens_it()
    {
        var engine = CreateEngine();
        engine.SetPlainSelection(new CursorPosition(5, 0), new CursorPosition(5, 3));
        Assert.True(IsOpen(engine));
    }

    [Fact]
    public void Search_into_a_closed_fold_opens_it()
    {
        var engine = CreateEngine();
        foreach (var key in new[] { "/", "l", "i", "n", "e", "5", "Return" })
            engine.ProcessKey(key);

        Assert.Equal(5, engine.Cursor.Line);
        Assert.True(IsOpen(engine));
    }

    [Fact]
    public void Ex_line_number_into_a_closed_fold_opens_it()
    {
        var engine = CreateEngine();
        foreach (var key in new[] { ":", "5", "Return" })
            engine.ProcessKey(key);

        Assert.Equal(4, engine.Cursor.Line);
        Assert.True(IsOpen(engine));
    }

    [Fact]
    public void Count_G_into_a_closed_fold_opens_it()
    {
        var engine = CreateEngine();
        engine.ProcessKey("4");
        engine.ProcessKey("G", shift: true);

        Assert.Equal(3, engine.Cursor.Line);
        Assert.True(IsOpen(engine));
    }

    [Fact]
    public void Jump_back_into_a_closed_fold_opens_it()
    {
        var engine = CreateEngine();
        engine.SetCursorPosition(new CursorPosition(4, 0));   // 開く
        // 4 行目と 9 行目をジャンプリストに積んで 10 行目へ（このエンジンの Ctrl+O は、最後に積んだものの 1 つ前＝4 行目へ戻る）
        foreach (var key in new[] { "/", "l", "i", "n", "e", "9", "Return", "/", "l", "i", "n", "e", "1", "Return" })
            engine.ProcessKey(key);
        engine.CurrentBuffer.Folds.CloseAll();                 // 留守の間に畳まれた

        engine.ProcessKey("o", ctrl: true);

        Assert.Equal(4, engine.Cursor.Line);
        Assert.True(IsOpen(engine));
    }

    [Fact]
    public void Line_by_line_motion_skips_the_fold_without_opening_it()
    {
        var engine = CreateEngine();
        engine.SetCursorPosition(new CursorPosition(1, 0));
        engine.ProcessKey("j");
        engine.ProcessKey("j");

        Assert.False(IsOpen(engine));
    }
}

/// <summary><c>:行番号</c> がエンジンの位置ごと動くこと（範囲の剥がしで「Not an editor command」になっていた不具合）。</summary>
public class ExLineNumberTests
{
    private static VimEngine CreateEngine()
    {
        var engine = new VimEngine(new VimConfig());
        engine.SetText("a\nb\nc\nd\ne\nf");
        return engine;
    }

    private static void Ex(VimEngine engine, string command)
    {
        engine.ProcessKey(":");
        foreach (var c in command)
            engine.ProcessKey(c.ToString());
        engine.ProcessKey("Return");
    }

    [Theory]
    [InlineData("5", 4)]
    [InlineData("$", 5)]
    [InlineData("99", 5)]   // 末尾に丸める
    public void Moves_the_engine_cursor(string command, int line)
    {
        var engine = CreateEngine();
        Ex(engine, command);
        Assert.Equal(line, engine.Cursor.Line);
    }

    [Fact]
    public void Next_motion_continues_from_the_new_line_and_quote_quote_returns()
    {
        var engine = CreateEngine();
        Ex(engine, "4");
        engine.ProcessKey("j");
        Assert.Equal(4, engine.Cursor.Line);

        engine.ProcessKey("'");
        engine.ProcessKey("'");   // '' で :4 の前の位置へ
        Assert.Equal(0, engine.Cursor.Line);
    }
}
