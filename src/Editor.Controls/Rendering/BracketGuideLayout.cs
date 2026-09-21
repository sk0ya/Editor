using Editor.Core.Editing;
using Editor.Core.Models;

namespace Editor.Controls.Rendering;

/// <summary>
/// 括弧ペアを結ぶ縦線の「どこからどこまで」を決める係。バッファ全体の走査結果
/// (<see cref="BracketGuideScanner"/>) を行配列ごとにキャッシュし、いま見えている行だけを
/// 画面座標の区間に落とす。描画そのものは <see cref="OverlayRenderer.DrawBracketGuides"/>。
/// </summary>
/// <remarks>
/// 走査はバッファ全体 1 パスなので、打鍵ごとに走らせないよう行配列の参照でキャッシュする
/// （<c>TextBuffer</c> のスナップショットは編集のたびに別配列になるので、参照比較で足りる）。
/// </remarks>
internal sealed class BracketGuideLayout
{
    /// <summary>見えている表示行 1 行ぶん。折り返された行は複数行になるので、その先頭/末尾かも持つ。</summary>
    internal readonly record struct Row(
        int BufferLine, double Y, bool IsCodeLens, bool IsFirstRowOfLine, bool IsLastRowOfLine);

    private string[]? _scannedLines;
    private IReadOnlyList<BracketGuide> _guides = [];
    private readonly List<OverlayRenderer.BracketGuideSegment> _segments = [];
    private readonly List<OverlayRenderer.BracketGuideSegment> _activeSegments = [];

    public bool Enabled { get; private set; } = true;

    public BracketGuideSyntax Syntax { get; private set; } = BracketGuideSyntax.CFamily;

    /// <summary>設定を差し替える。実際に変わったときだけ true（呼び出し側の再描画判断用）。</summary>
    public bool Configure(bool enabled, BracketGuideSyntax? syntax)
    {
        syntax ??= BracketGuideSyntax.CFamily;
        if (Enabled == enabled && Syntax == syntax) return false;

        Enabled = enabled;
        Syntax = syntax;
        Invalidate();
        return true;
    }

    public void Invalidate()
    {
        _scannedLines = null;
        _guides = [];
    }

    private IReadOnlyList<BracketGuide> GuidesFor(string[] lines)
    {
        if (ReferenceEquals(_scannedLines, lines)) return _guides;
        _guides = BracketGuideScanner.Build(lines, Syntax);
        _scannedLines = lines;
        return _guides;
    }

    /// <summary>
    /// 見えている行ぶんの縦線を組み立てる。開き括弧の行は行の下半分、閉じ括弧の行は上半分だけを
    /// 引くので、線は括弧そのものから伸びて括弧で止まる。カーソルのいるいちばん内側のブロックは
    /// <c>Active</c> として最後に返す（同じ桁で重なっても強調側が上に乗る）。
    /// </summary>
    /// <param name="measureX">(行, 桁) → 画面上の X。タブや全角があるので実測に任せる。</param>
    public IReadOnlyList<OverlayRenderer.BracketGuideSegment> Build(
        string[] lines, IReadOnlyList<Row> rows, CursorPosition cursor,
        Func<int, int, double> measureX, double lineHeight)
    {
        _segments.Clear();
        _activeSegments.Clear();
        if (!Enabled || rows.Count == 0 || lineHeight <= 0) return _segments;

        var guides = GuidesFor(lines);
        if (guides.Count == 0) return _segments;

        int firstBufferLine = int.MaxValue, lastBufferLine = int.MinValue;
        foreach (var row in rows)
        {
            if (row.BufferLine < firstBufferLine) firstBufferLine = row.BufferLine;
            if (row.BufferLine > lastBufferLine) lastBufferLine = row.BufferLine;
        }

        int active = BracketGuideScanner.ActiveIndex(guides, cursor.Line, cursor.Column);

        for (int i = 0; i < guides.Count; i++)
        {
            var guide = guides[i];
            if (guide.OpenLine > lastBufferLine || guide.CloseLine < firstBufferLine) continue;
            if (guide.AnchorLine >= lines.Length) continue;

            double x = measureX(guide.AnchorLine, guide.AnchorColumn);
            bool isActive = i == active;
            var into = isActive ? _activeSegments : _segments;

            foreach (var row in rows)
            {
                if (row.BufferLine < guide.OpenLine || row.BufferLine > guide.CloseLine) continue;

                double top = row.Y, bottom = row.Y + lineHeight;
                if (row.BufferLine == guide.OpenLine)
                {
                    // 折り返された行では、括弧のある行の最後の表示行から下ろす
                    if (row.IsCodeLens || !row.IsLastRowOfLine) continue;
                    top = row.Y + lineHeight / 2;
                }
                else if (row.BufferLine == guide.CloseLine)
                {
                    if (row.IsCodeLens || !row.IsFirstRowOfLine) continue;
                    bottom = row.Y + lineHeight / 2;
                }

                into.Add(new OverlayRenderer.BracketGuideSegment(x, top, bottom, isActive));
            }
        }

        _segments.AddRange(_activeSegments);
        return _segments;
    }
}
