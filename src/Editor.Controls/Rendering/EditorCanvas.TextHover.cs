using System;
using System.Collections.Generic;
using System.Windows;
using Editor.Core.Lsp;

namespace Editor.Controls.Rendering;

/// <summary>
/// EditorCanvas の<b>本文ホバー</b>（Rider/VS の「マウスを乗せると型と説明が出る」やつ）の下ごしらえ。
/// ここは「今どの語の上にいるか」だけを見て <see cref="TextHoverChanged"/> を上げる薄い層で、
/// hover の問い合わせもポップアップも <c>VimEditorControl</c> 側が持つ。
///
/// <para>デバッグ中の DataTip（<c>EditorCanvas.Breakpoints.cs</c>）と作りは似ているが別物：
/// あちらは <c>a.b.c</c> の式チェーンを切り出してデバッガに評価させるもので、停止中しか動かない。
/// こちらは<b>常時</b>動き、語の範囲だけを渡して言語サーバーに聞く。</para>
///
/// <para>マウスが「文字の上」にあるかは自前で判定する——<c>HitTest</c> は行末より右でも
/// 最終桁へ丸めるので、それだけを信じると<b>行末の空白に乗せただけで最後の語のツールチップが出る</b>。</para>
/// </summary>
public partial class EditorCanvas
{
    private bool _textHoverEnabled = true;
    private int _textHoverLine = -1, _textHoverStart = -1, _textHoverEnd = -1;

    /// <summary>本文の語の上にマウスが止まった（＝ホバー対象が変わった）とき。</summary>
    public event Action<TextHover>? TextHoverChanged;

    /// <summary>ホバーが語から外れたとき。ホストはポップアップを閉じる。</summary>
    public event Action? TextHoverEnded;

    /// <summary>本文ホバー通知の有効/無効。無効化すると進行中のホバーも終了する。</summary>
    public void SetTextHoverEnabled(bool enabled)
    {
        if (_textHoverEnabled == enabled) return;
        _textHoverEnabled = enabled;
        if (!enabled) ClearTextHover();
    }

    /// <summary>その位置に重なっている診断（エラー・警告）。ホバーの本文に添えるためホストが読む。</summary>
    public IReadOnlyList<LspDiagnostic> DiagnosticsAt(int line, int column)
    {
        List<LspDiagnostic>? hits = null;
        foreach (var diag in _diagnostics)
        {
            if (!ContainsPosition(diag.Range, line, column)) continue;
            (hits ??= []).Add(diag);
        }
        return hits ?? (IReadOnlyList<LspDiagnostic>)[];
    }

    private static bool ContainsPosition(LspRange range, int line, int column)
    {
        if (line < range.Start.Line || line > range.End.Line) return false;
        if (line == range.Start.Line && column < range.Start.Character) return false;
        if (line != range.End.Line) return true;
        // 空範囲（start == end）はその 1 文字を指しているものとして扱う。ただし桁を広げてよいのは
        // 1 行に収まる範囲だけ——複数行の範囲で終端行にこれを効かせると、最終行の先頭 1 文字が
        // 常に「中」になり、無関係な語のホバーに他所のエラーが出る。
        var end = range.Start.Line == range.End.Line
            ? Math.Max(range.End.Character, range.Start.Character + 1)
            : range.End.Character;
        return column < end;
    }

    /// <summary>本文上の点でホバーしている語を判定し、変化があれば通知する。</summary>
    private void UpdateTextHover(Point point)
    {
        if (!_textHoverEnabled || TextHoverChanged is null) return;

        if (!TryHitTestWord(point, out int line, out int start, out int end))
        {
            ClearTextHover();
            return;
        }

        // 同じ語を指している間は再通知しない（サーバーへの連打を防ぐ）。
        if (line == _textHoverLine && start == _textHoverStart && end == _textHoverEnd) return;
        _textHoverLine = line; _textHoverStart = start; _textHoverEnd = end;

        // ポップアップは語の下に出したいので、ホバー位置を 1 行分下げたアンカーを渡す。
        TextHoverChanged.Invoke(new TextHover(
            line, start, end, new Point(point.X, point.Y + _lineHeight)));
    }

    private void ClearTextHover()
    {
        if (_textHoverLine < 0) return;
        _textHoverLine = _textHoverStart = _textHoverEnd = -1;
        TextHoverEnded?.Invoke();
    }

    /// <summary>点の下にある語（識別子）の範囲。文字の上に無ければ false。
    /// （テストからも直接呼ぶ——マウス移動の合成より、この判定そのものを確かめたい。）</summary>
    internal bool TryHitTestWord(Point point, out int line, out int start, out int end)
    {
        line = start = end = -1;
        if (_lineHeight <= 0 || _charWidth <= 0 || _lines.Length == 0) return false;

        var (_, _, _, _, gutterWidth) = GetGutterMetrics();
        if (point.X < gutterWidth) return false;

        // Y は行へ丸められるので、最終行より下は自分で弾く。
        int visualLine = (int)((point.Y + _scrollOffsetY) / _lineHeight);
        if (point.Y < 0 || visualLine < 0 || visualLine >= TotalVisualLines) return false;

        var (hitLine, col) = HitTest(point);
        if (hitLine < 0 || hitLine >= _lines.Length) return false;
        var text = _lines[hitLine];
        if (col < 0 || col >= text.Length) return false;
        if (!IsInsideText(point, visualLine, text)) return false;
        if (!IsWordChar(text[col])) return false;

        int s = col, e = col;
        while (s > 0 && IsWordChar(text[s - 1])) s--;
        while (e < text.Length - 1 && IsWordChar(text[e + 1])) e++;

        line = hitLine; start = s; end = e;
        return true;
    }

    /// <summary>点が「その行に実際に文字がある範囲」に入っているか（行末より右なら false）。</summary>
    private bool IsInsideText(Point point, int visualLine, string lineText)
    {
        double visualX = point.X - GetGutterMetrics().gutterWidth + (_wrapLines ? 0 : _scrollOffsetX);
        if (visualX < 0) return false;

        if (!_wrapLines) return visualX < GetVisualX(lineText, lineText.Length);

        // 折り返し中はこの表示行が担当する範囲だけが「その行の文字」。
        var segment = GetVisualSegment(visualLine);
        int segStart = Math.Min(segment.StartColumn, lineText.Length);
        int segEnd = Math.Min(GetSegmentEndColumn(visualLine), lineText.Length);
        if (segEnd <= segStart) return false;
        var segmentText = lineText.Substring(segStart, segEnd - segStart);
        return visualX < GetVisualX(segmentText, segmentText.Length);
    }

    /// <summary>語を構成する文字。日本語（や他の非 ASCII）の識別子・文字列の中身も 1 語として扱う——
    /// 位置さえ渡せばサーバーが範囲を決めるので、こちらは広めに拾って構わない。</summary>
    private static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c == '_' || c == '$' || c > 0x7F;
}

/// <summary>本文ホバーの通知。<paramref name="Anchor"/> はポップアップを出すキャンバス相対座標（当該行の下端）。
/// <paramref name="StartColumn"/>/<paramref name="EndColumn"/> は語の範囲（<b>両端を含む</b>）で、
/// 同じ語の上でマウスが動いただけかを見分けるために使う。</summary>
public readonly record struct TextHover(int Line, int StartColumn, int EndColumn, Point Anchor);
