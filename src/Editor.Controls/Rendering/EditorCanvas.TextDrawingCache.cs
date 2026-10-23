using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;
using Editor.Controls.Themes;
using Editor.Core.Engine;
using Editor.Core.Lsp;
using Editor.Core.Syntax;

namespace Editor.Controls.Rendering;

/// <summary>
/// EditorCanvas の描画を3層に分け、本文の絵を作り置きして使い回す。
/// <para><b>なぜ</b>：キャレットが1つ動く・カーソルが点滅するだけでも <see cref="OnRender"/> は画面全体を描き直す。
/// 以前はそのたびに見えている全行を <see cref="FormattedText"/> で組み直し（1400×1000 の窓で 1フレーム 6〜9ms、
/// j/k の時間の 7 割）、さらに組んだグリフをフレームごとに合成スレッドへ送り直していた（描画内容はフレームごとに
/// 丸ごと差し替わるので、前のフレームの絵が一度解放されてから登録し直される）。</para>
/// <para><b>形</b>：重ね順は従来の1枚描きと同じ。</para>
/// <list type="number">
/// <item>下層＝キャンバス自身の <see cref="OnRender"/>：背景・現在行・選択・検索などの強調・ガター。</item>
/// <item>本文層（<see cref="_textLayer"/>）：見えている行ごとに常駐する子ビジュアル。中身（色分け済みの
/// グリフ）は行の文字・字句・診断の装飾・書体・テーマが同じなら作り直さず、スクロールは位置を動かすだけ。</item>
/// <item>上層（<see cref="_overlayLayer"/>）：本文より上に描いていたもの（カーソル・波線・リンクの下線・
/// 行末の値・ポップアップ・スクロールバー…）。従来どおり毎フレーム描く。</item>
/// </list>
/// <para>表の列揃え（Markdown）・IME の合成中・置換プレビュー・長大な行は、桁の位置が実バッファと噛み合わない
/// 特別な描き方なので作り置かず、その行のビジュアルへ毎フレーム描く（<see cref="DrawLineText"/>）。</para>
/// </summary>
public partial class EditorCanvas
{
    /// <summary>1行ぶんの色分け済みの本文の絵。行の文字列と字句の配列（の参照）で引き、残りの入力は中で照合する。</summary>
    private sealed class LineTextDrawing
    {
        public required int LineIndex;
        public required object? SemanticTokens;
        public required IReadOnlyList<LspDiagnostic>? Diagnostics;
        public required EditorTheme Theme;
        public required Brush Foreground;
        public required Typeface Typeface;
        public required double FontSize;
        public required double PixelsPerDip;
        public required Drawing Drawing;
        public required double Height;
        public int LastUsedFrame;
    }

    /// <summary>行の文字列と字句の配列を<b>参照</b>で比べるキー。編集で変わらなかった行は、行番号がずれても
    /// 同じ文字列・同じ字句配列のまま残るので（TextBuffer のスナップショットと差分の字句分け）、Enter で行が
    /// 増えても下の行の絵は作り直さない。
    /// <para>折り返した行は段（<paramref name="StartColumn"/>）ごとに別の絵にする。1つの絵を段どうしで共有すると、
    /// WPF は最初に実体化した段の横のサブピクセル位置で他の段の文字も描き、スクロールの経緯によって縁の
    /// アンチエイリアスが変わった（従来は段ごとに文字を組んでいた）。</para></summary>
    private readonly record struct LineTextKey(string Text, SyntaxToken[]? Tokens, int StartColumn)
    {
        public bool Equals(LineTextKey other) =>
            ReferenceEquals(Text, other.Text) && ReferenceEquals(Tokens, other.Tokens) && StartColumn == other.StartColumn;
        public override int GetHashCode() =>
            HashCode.Combine(RuntimeHelpers.GetHashCode(Text), Tokens is null ? 0 : RuntimeHelpers.GetHashCode(Tokens), StartColumn);
    }

    /// <summary>本文層の1行。縦の位置だけを <see cref="ContainerVisual.Offset"/> で持ち、縦スクロールでは
    /// 絵を描き直さない。横の位置（ガターの幅・横スクロール）と行の切り抜きは中身に焼き込み、変わったときだけ
    /// 置き直す（横スクロールは縦に比べてまれ）。</summary>
    private sealed class TextRowVisual : DrawingVisual
    {
        public Drawing? Content;
        public Point ContentOrigin;
        public Rect ClipRect;
        public int LastUsedFrame;
        /// <summary>中身を描いた回数（縦スクロールで描き直していないことをテストで確かめる）。</summary>
        public int ContentRenders;
    }

    private readonly Dictionary<LineTextKey, LineTextDrawing> _lineTextDrawings = [];
    // 作り置きの絵を出している行（絵＋折り返しの区切りの桁ごと）。
    private readonly Dictionary<(LineTextDrawing Drawing, int StartColumn), TextRowVisual> _textRows = [];
    // 作り置かない行（と、同じ絵が同じフレームに2度出る行）。ビジュアル行の番号ごと、毎フレーム描く。
    private readonly Dictionary<int, TextRowVisual> _volatileTextRows = [];

    private readonly ContainerVisual _textLayer = new();
    private readonly DrawingVisual _overlayLayer = new();
    private readonly VisualCollection _layers;
    private Rect _textLayerClip = Rect.Empty;

    // 行番号など、短い文字列の描画（文字列＋色ごと）。書体・DPI が変わったら捨てる。
    private readonly Dictionary<(string Text, Brush Brush), (Drawing Drawing, double Height)> _labelDrawings = [];
    private (Typeface Typeface, double FontSize, double PixelsPerDip) _labelDrawingsFont;
    private const int LabelDrawingCacheLimit = 4096;
    // 斜体・縮小で組む短い文字列（CodeLens のラベル）。行番号と同じく書体・DPI が変わったら捨てる。
    private readonly Dictionary<(string Text, Brush Brush, double Scale), TextLabel> _scaledLabels = [];

    private int _renderFrame;
    // 描画中だけ有効な DPI（VisualTreeHelper.GetDpi は毎回木をたどるので、1フレームに1回だけ引く）。
    private double _frameDpi = double.NaN;

    protected override int VisualChildrenCount => _layers.Count;

    protected override Visual GetVisualChild(int index) => _layers[index];

    protected override void OnRender(DrawingContext dc)
    {
        BeginTextDrawingFrame();
        try
        {
            using var over = _overlayLayer.RenderOpen();
            RenderLayers(dc, over);
        }
        finally
        {
            _frameDpi = double.NaN;
        }
    }

    /// <summary>描画のたびに1つ進める。このフレームで使われなかった行・作り置きを捨てる目安になる。</summary>
    private void BeginTextDrawingFrame()
    {
        _renderFrame++;
        _frameDpi = GetDpi();
    }

    /// <summary>本文層の切り抜き（本文の領域）。変わったときだけ差し替える。</summary>
    private void SetTextLayerClip(Rect clip)
    {
        if (clip == _textLayerClip) return;
        _textLayerClip = clip;
        var geometry = new RectangleGeometry(clip);
        geometry.Freeze();
        _textLayer.Clip = geometry;
    }

    /// <summary>このフレームで使わなかった行のビジュアルを外し、膨らんだ作り置きを捨てる。</summary>
    private void EndTextDrawingFrame(int visibleRows)
    {
        RemoveUnused(_textRows);
        RemoveUnused(_volatileTextRows);

        if (_lineTextDrawings.Count > visibleRows * 2 + 64)
        {
            var stale = _lineTextDrawings.Where(pair => pair.Value.LastUsedFrame != _renderFrame).Select(pair => pair.Key).ToArray();
            foreach (var key in stale)
                _lineTextDrawings.Remove(key);
        }

        void RemoveUnused<TKey>(Dictionary<TKey, TextRowVisual> rows) where TKey : notnull
        {
            List<TKey>? unused = null;
            foreach (var (key, row) in rows)
                if (row.LastUsedFrame != _renderFrame) (unused ??= []).Add(key);
            if (unused is null) return;
            foreach (var key in unused)
            {
                _textLayer.Children.Remove(rows[key]);
                rows.Remove(key);
            }
        }
    }

    /// <summary>
    /// ビジュアル行 <paramref name="visualLine"/>（バッファ行 <paramref name="lineIndex"/>）の本文を本文層に置く。
    /// <paramref name="textLeft"/>/<paramref name="y"/> は従来の1枚描きと同じ座標。横スクロールの量は
    /// 呼び出し時点の <see cref="_scrollOffsetX"/>（折り返しなら区切りの桁の位置）。
    /// </summary>
    private void PlaceTextRow(int visualLine, int lineIndex, int startColumn, string lineText, double y, double textLeft, double width)
    {
        if (string.IsNullOrEmpty(lineText)) return; // 描くものが無い（DrawLineText と同じ）
        var clipSize = new Size(Math.Max(0, width - textLeft), _lineHeight);

        if (IsCacheableLineText(lineIndex, lineText))
        {
            var drawing = GetLineTextDrawing(lineIndex, lineText, startColumn);
            if (!_textRows.TryGetValue((drawing, startColumn), out var row))
            {
                row = new TextRowVisual();
                _textRows[(drawing, startColumn)] = row;
                _textLayer.Children.Add(row);
            }
            if (row.LastUsedFrame != _renderFrame)
            {
                row.LastUsedFrame = _renderFrame;
                var origin = new Point(textLeft - _scrollOffsetX, (_lineHeight - drawing.Height) / 2);
                var clipRect = new Rect(new Point(textLeft, 0), clipSize);
                if (!ReferenceEquals(row.Content, drawing.Drawing) || row.ContentOrigin != origin || row.ClipRect != clipRect)
                {
                    row.Content = drawing.Drawing;
                    row.ContentOrigin = origin;
                    row.ClipRect = clipRect;
                    row.ContentRenders++;
                    using var context = row.RenderOpen();
                    PushFrozenClip(context, clipRect);
                    PushFrozenTranslate(context, origin);
                    context.DrawDrawing(drawing.Drawing);
                    context.Pop();
                    context.Pop();
                }
                SetRowTop(row, y);
                return;
            }
            // 同じ絵が同じフレームにもう1度（同じ文字列を貼り重ねた行など）：この行は毎フレーム描く側で出す。
        }

        if (!_volatileTextRows.TryGetValue(visualLine, out var volatileRow))
        {
            volatileRow = new TextRowVisual();
            _volatileTextRows[visualLine] = volatileRow;
            _textLayer.Children.Add(volatileRow);
        }
        volatileRow.LastUsedFrame = _renderFrame;
        volatileRow.Content = null;
        using (var context = volatileRow.RenderOpen())
        {
            PushFrozenClip(context, new Rect(new Point(textLeft, 0), clipSize));
            DrawLineText(context, lineIndex, lineText, 0, textLeft);
            context.Pop();
        }
        SetRowTop(volatileRow, y);
    }

    private static void SetRowTop(TextRowVisual row, double y)
    {
        var offset = new Vector(0, y);
        if (row.Offset != offset) row.Offset = offset;
    }

    private static void PushFrozenClip(DrawingContext context, Rect rect)
    {
        var clip = new RectangleGeometry(rect);
        clip.Freeze();
        context.PushClip(clip);
    }

    private static void PushFrozenTranslate(DrawingContext context, Point origin)
    {
        var transform = new TranslateTransform(origin.X, origin.Y);
        transform.Freeze();
        context.PushTransform(transform);
    }

    /// <summary>作り置きの絵で出してよい行か。<see cref="DrawLineText"/> の特別な描き方に当たらない行。</summary>
    private bool IsCacheableLineText(int lineIndex, string lineText) =>
        lineText.Length <= LongLineCharacterThreshold
        && !(lineIndex == _cursor.Line && _mode is VimMode.Insert or VimMode.Replace && !string.IsNullOrEmpty(_imeCompositionText))
        && !_substitutePreviewLines.ContainsKey(lineIndex)
        && _activeLineOverrides is not { Count: > 0 };

    /// <summary>その行の色分け済みの絵。作り置きがあり、入力が何も変わっていなければそれを返す。</summary>
    private LineTextDrawing GetLineTextDrawing(int lineIndex, string lineText, int startColumn)
    {
        var tokens = TokensForLine(lineIndex);
        _semanticTokensByLine.TryGetValue(lineIndex, out var semantic);
        var diagnostics = _decoratingDiagnostics ? _diagnostics : null;
        var theme = Theme;
        double pixelsPerDip = GetDpi();
        var key = new LineTextKey(lineText, tokens, startColumn);

        if (!_lineTextDrawings.TryGetValue(key, out var entry)
            || !ReferenceEquals(entry.SemanticTokens, semantic)
            || !ReferenceEquals(entry.Diagnostics, diagnostics)
            // 意味トークンと診断は行番号で引くので、行がずれたら（中身が同じでも）作り直す
            || ((semantic is not null || diagnostics is not null) && entry.LineIndex != lineIndex)
            || !ReferenceEquals(entry.Theme, theme)
            || !ReferenceEquals(entry.Foreground, theme.Foreground)
            || !ReferenceEquals(entry.Typeface, _typeface)
            || entry.FontSize != _fontSize
            || entry.PixelsPerDip != pixelsPerDip)
        {
            var (drawing, height) = BuildLineTextDrawing(lineIndex, lineText);
            entry = new LineTextDrawing
            {
                LineIndex = lineIndex, SemanticTokens = semantic, Diagnostics = diagnostics,
                Theme = theme, Foreground = theme.Foreground, Typeface = _typeface, FontSize = _fontSize,
                PixelsPerDip = pixelsPerDip, Drawing = drawing, Height = height,
            };
            _lineTextDrawings[key] = entry;
        }
        entry.LastUsedFrame = _renderFrame;
        return entry;
    }

    /// <summary>
    /// 行を1つの <see cref="FormattedText"/> に組み、字句ごとの色・意味トークンの斜体／取り消し線・
    /// 診断の薄字／取り消し線を載せて、原点に描いた絵にする（従来の <see cref="DrawLineTextWithSegments"/>
    /// の速い経路と同じ組み方——行全体を1つで組むので字間・合字がキャレットの位置計算と一致する）。
    /// </summary>
    private (Drawing Drawing, double Height) BuildLineTextDrawing(int lineIndex, string lineText)
    {
        var ft = FormatText(lineText, Theme.Foreground);
        var segments = BuildColorSegments(lineIndex, lineText.Length);
        if (segments != null)
        {
            foreach (var (startCol, length, brush, italic, deprecated) in segments)
            {
                int start = Math.Clamp(startCol, 0, lineText.Length);
                int count = Math.Clamp(length, 0, lineText.Length - start);
                if (count <= 0) continue;
                if (brush != null) ft.SetForegroundBrush(brush, start, count);
                ApplySemanticModifierStyle(ft, start, count, italic, deprecated);
            }
        }
        ApplyDiagnosticDecorations(ft, lineIndex, lineText, 0, lineText.Length);

        var group = new DrawingGroup();
        using (var context = group.Open())
            context.DrawText(ft, new Point(0, 0));
        return (group, ft.Height);
    }

    /// <summary>
    /// 短い文字列（行番号）を、行の高さの中で縦に中央へ描く。同じ文字列・同じ色の絵は使い回す——
    /// 行番号は j/k のたびに全行ぶん組み直していたが、出てくる文字列は数えるほどしかない。
    /// </summary>
    internal void DrawLabelText(DrawingContext dc, string text, Brush brush, double x, double rowTop)
    {
        if (text.Length == 0) return;
        EnsureLabelFont();
        if (!_labelDrawings.TryGetValue((text, brush), out var label))
        {
            if (_labelDrawings.Count >= LabelDrawingCacheLimit) _labelDrawings.Clear();
            var ft = FormatText(text, brush);
            var group = new DrawingGroup();
            using (var context = group.Open())
                context.DrawText(ft, new Point(0, 0));
            label = (group, ft.Height);
            _labelDrawings[(text, brush)] = label;
        }
        var transform = new TranslateTransform(x, rowTop + (_lineHeight - label.Height) / 2);
        transform.Freeze();
        dc.PushTransform(transform);
        dc.DrawDrawing(label.Drawing);
        dc.Pop();
    }

    /// <summary>CodeLens のラベルのような、斜体・縮小で組む短い文字列。同じ文字列・色・倍率なら組んだ結果を使い回す
    /// ——C# ではメンバーごとに「N 個の参照」が出て、以前は1フレームごとに全部組み直していた。</summary>
    internal TextLabel ScaledLabel(string text, Brush brush, double sizeScale)
    {
        EnsureLabelFont();
        if (_scaledLabels.TryGetValue((text, brush, sizeScale), out var label)) return label;
        if (_scaledLabels.Count >= LabelDrawingCacheLimit) _scaledLabels.Clear();
        var formatted = FormatScaledText(text, brush, sizeScale);
        var group = new DrawingGroup();
        using (var context = group.Open())
            context.DrawText(formatted, new Point(0, 0));
        label = new TextLabel(group, formatted.Width, formatted.Height);
        _scaledLabels[(text, brush, sizeScale)] = label;
        return label;
    }

    /// <summary>書体・大きさ・DPI が変わっていたら、短い文字列の作り置きを捨てる。</summary>
    private void EnsureLabelFont()
    {
        var font = (_typeface, _fontSize, GetDpi());
        if (_labelDrawingsFont == font) return;
        _labelDrawings.Clear();
        _scaledLabels.Clear();
        _labelDrawingsFont = font;
    }
}
