using Editor.Core.Models;

namespace Editor.Core.Lsp;

/// <summary>
/// エディタの選択を LSP の <see cref="LspRange"/> へ変換する。
///
/// <para><b>ここは規約が2つ食い違う場所。</b>エディタの <see cref="Selection"/> は
/// <b>終端を含むセル範囲</b>（<c>GetSelectionText</c> が <c>End.Column + 1</c> で切り出す）で、
/// LSP の range は<b>終端を含まない</b>。さらに行選択は「行まるごと」の意味なのに、
/// <see cref="Selection.Start"/> にはキャレットの桁が残っている。</para>
///
/// <para>そのまま渡すと、行選択したのに<b>文の途中から始まる範囲</b>を送ることになり、
/// 「メソッドの抽出」のように<b>完全な文の並びを要求する</b>リファクタリングが
/// 1件も返らない（実測: <c>range=30,27-31,0</c> で 0 件、行頭からに直すと候補が出る）。
/// リファクタリングが「出ない」ときに最初に疑う場所（Loomo 設計書 §32.4.1）。</para>
/// </summary>
public static class EditorSelectionRange
{
    /// <param name="lineLength">行番号（0始まり）→ その行の長さ。範囲外は 0 を返してよい。</param>
    /// <returns>選択が空なら null。</returns>
    public static LspRange? FromSelection(Selection selection, Func<int, int> lineLength)
    {
        var start = selection.NormalizedStart;
        var end = selection.NormalizedEnd;

        // 行選択は「1行だけ」でも行まるごとを指す。IsEmpty（Start == End）で弾いてはいけない——
        // V を押しただけの状態が「選択なし」に化け、キャレット1点で問い合わせることになる。
        if (selection.Type == SelectionType.Line)
            return new LspRange(
                new LspPosition(start.Line, 0),
                new LspPosition(end.Line, lineLength(end.Line)));

        if (selection.IsEmpty) return null;

        return selection.Type switch
        {
            // 矩形選択は外接する矩形（LSP に矩形を表す手段が無い）。
            SelectionType.Block => new LspRange(
                new LspPosition(start.Line, Math.Min(start.Column, end.Column)),
                new LspPosition(end.Line, Exclusive(Math.Max(start.Column, end.Column), end.Line, lineLength))),

            _ => new LspRange(
                new LspPosition(start.Line, start.Column),
                new LspPosition(end.Line, Exclusive(end.Column, end.Line, lineLength))),
        };
    }

    /// <summary>「含む」桁を「含まない」桁へ。行末を越えないよう丸める。</summary>
    private static int Exclusive(int inclusiveColumn, int line, Func<int, int> lineLength)
        => Math.Min(inclusiveColumn + 1, lineLength(line));
}
