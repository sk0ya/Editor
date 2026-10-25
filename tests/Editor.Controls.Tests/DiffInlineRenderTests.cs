using System.Reflection;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Editor.Controls.Rendering;
using Editor.Core.Editing;

namespace Editor.Controls.Tests;

/// <summary>
/// 差分表示の上乗せ（<see cref="DiffDecorations.InlineChanges"/> の行内差分・スクロールバーの変更位置の印）。
/// 行内差分は「範囲の文字だけが塗られる」ことを、範囲あり／なしの描画の差分画素で確かめる。
/// </summary>
public sealed class DiffInlineRenderTests
{
    private const int Width = 400;
    private const int Height = 120;

    private static DiffDecorations Decorations(
        Dictionary<int, DiffDecorationKind> lines,
        Dictionary<int, IReadOnlyList<DiffInlineRange>>? inline = null,
        Dictionary<int, int>? spacers = null)
        => new(lines, spacers ?? new Dictionary<int, int>(), inline);

    private static byte[] Render(EditorCanvas canvas)
    {
        canvas.UpdateLayout();
        var bitmap = new RenderTargetBitmap(Width, Height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(canvas);
        var pixels = new byte[Width * Height * 4];
        bitmap.CopyPixels(pixels, Width * 4, 0);
        return pixels;
    }

    /// <summary>2枚の描画で色が違う画素の x 座標（行 <paramref name="row"/> の中央の高さで）。</summary>
    private static List<int> ChangedColumns(byte[] before, byte[] after, EditorCanvas canvas, int row)
    {
        var y = (int)(row * canvas.LineHeight + canvas.LineHeight / 2);
        var columns = new List<int>();
        for (var x = 0; x < Width; x++)
        {
            var i = (y * Width + x) * 4;
            if (before[i] != after[i] || before[i + 1] != after[i + 1] || before[i + 2] != after[i + 2])
                columns.Add(x);
        }
        return columns;
    }

    private static double VisualX(EditorCanvas canvas, int line, string text, int column)
    {
        typeof(EditorCanvas).GetMethod("SetActiveLine", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(canvas, [line]);
        return (double)typeof(EditorCanvas).GetMethod("GetVisualX", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(canvas, [text, column])!;
    }

    private static EditorCanvas Canvas(params string[] lines)
    {
        var canvas = new EditorCanvas();
        canvas.UpdateFont("Consolas", 14);
        canvas.SetLines(lines);
        return canvas;
    }

    [Fact]
    public void Inline_range_paints_exactly_the_changed_characters()
    {
        WpfTestHost.Run(() =>
        {
            // 空白だけの行にすると文字の画素が混ざらず、塗った背景だけが差分に出る。
            const string text = "          ";
            var canvas = Canvas("x", text, "y");
            var window = WpfTestHost.Load(canvas, Width, Height);
            try
            {
                var lines = new Dictionary<int, DiffDecorationKind> { [1] = DiffDecorationKind.Removed };
                canvas.SetDiffDecorations(Decorations(lines));
                var plain = Render(canvas);

                canvas.SetDiffDecorations(Decorations(lines,
                    new Dictionary<int, IReadOnlyList<DiffInlineRange>> { [1] = [new DiffInlineRange(2, 3)] }));
                var marked = Render(canvas);

                var changed = ChangedColumns(plain, marked, canvas, row: 1);
                Assert.NotEmpty(changed);
                var expectedWidth = VisualX(canvas, 1, text, 5) - VisualX(canvas, 1, text, 2);
                Assert.InRange(changed[^1] - changed[0] + 1, expectedWidth - 2, expectedWidth + 2);
                // 隣の行には何も起きない
                Assert.Empty(ChangedColumns(plain, marked, canvas, row: 0));
                Assert.Empty(ChangedColumns(plain, marked, canvas, row: 2));
            }
            finally
            {
                window.Close();
            }
        });
    }

    /// <summary>
    /// 移動したブロックの行（<see cref="DiffDecorations.MovedLines"/>）は追加／削除と違う色で塗り、行内差分は描かない。
    /// </summary>
    [Fact]
    public void Moved_lines_get_their_own_color_and_no_inline_ranges()
    {
        WpfTestHost.Run(() =>
        {
            const string text = "          ";
            var canvas = Canvas("x", text, "y");
            var window = WpfTestHost.Load(canvas, Width, Height);
            try
            {
                var lines = new Dictionary<int, DiffDecorationKind> { [1] = DiffDecorationKind.Removed };
                var inline = new Dictionary<int, IReadOnlyList<DiffInlineRange>> { [1] = [new DiffInlineRange(2, 3)] };
                canvas.SetDiffDecorations(Decorations(lines));
                var removed = Render(canvas);

                canvas.SetDiffDecorations(new DiffDecorations(lines, new Dictionary<int, int>(), inline)
                {
                    MovedLines = new Dictionary<int, bool> { [1] = true },
                });
                var moved = Render(canvas);

                // 行全体の色が変わる（移動元の色）——行内差分の範囲だけでなく、行の端から端まで
                var changed = ChangedColumns(removed, moved, canvas, row: 1);
                Assert.True(changed.Count > Width / 2, $"changed={changed.Count}");

                // 行内差分の範囲は描かない：範囲を外しても同じ絵
                canvas.SetDiffDecorations(new DiffDecorations(lines, new Dictionary<int, int>())
                {
                    MovedLines = new Dictionary<int, bool> { [1] = true },
                });
                Assert.Empty(ChangedColumns(moved, Render(canvas), canvas, row: 1));
            }
            finally
            {
                window.Close();
            }
        });
    }

    /// <summary>
    /// キャレットのある変更行でも差分の色が消えない。現在行の強調はどのテーマでも不透明なので、差分の色を
    /// その下に敷くと消える——差分の次/前へ飛ぶとキャレットはまさにその変更行に乗るので、いちばん見たい行で
    /// 行の背景も行内差分も見えなくなっていた。
    /// </summary>
    [Fact]
    public void Diff_colors_stay_visible_on_the_caret_line()
    {
        WpfTestHost.Run(() =>
        {
            const string text = "          ";
            var canvas = Canvas("x", text, "y");
            canvas.IsActive = true;
            canvas.SetCursor(new Editor.Core.Models.CursorPosition(1, 0));
            var window = WpfTestHost.Load(canvas, Width, Height);
            try
            {
                canvas.SetDiffDecorations(DiffDecorations.Empty);
                var plain = Render(canvas);

                var lines = new Dictionary<int, DiffDecorationKind> { [1] = DiffDecorationKind.Added };
                canvas.SetDiffDecorations(Decorations(lines));
                var lineOnly = Render(canvas);
                // 行の背景：行の右のほう（キャレットの描画が届かない所）でも色が変わる。
                Assert.Contains(ChangedColumns(plain, lineOnly, canvas, row: 1), x => x > Width / 2);

                canvas.SetDiffDecorations(Decorations(lines,
                    new Dictionary<int, IReadOnlyList<DiffInlineRange>> { [1] = [new DiffInlineRange(4, 3)] }));
                var withInline = Render(canvas);
                var changed = ChangedColumns(lineOnly, withInline, canvas, row: 1);
                var expectedWidth = VisualX(canvas, 1, text, 7) - VisualX(canvas, 1, text, 4);
                Assert.NotEmpty(changed);
                Assert.InRange(changed[^1] - changed[0] + 1, expectedWidth - 2, expectedWidth + 2);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void Inline_range_on_a_line_without_a_diff_kind_is_not_drawn()
    {
        WpfTestHost.Run(() =>
        {
            var canvas = Canvas("          ", "          ");
            var window = WpfTestHost.Load(canvas, Width, Height);
            try
            {
                var lines = new Dictionary<int, DiffDecorationKind> { [0] = DiffDecorationKind.Added };
                canvas.SetDiffDecorations(Decorations(lines));
                var plain = Render(canvas);

                canvas.SetDiffDecorations(Decorations(lines,
                    new Dictionary<int, IReadOnlyList<DiffInlineRange>> { [1] = [new DiffInlineRange(0, 4)] }));
                var marked = Render(canvas);

                Assert.Empty(ChangedColumns(plain, marked, canvas, row: 1));
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void Inline_ranges_beyond_the_line_are_clamped_not_thrown()
    {
        WpfTestHost.Run(() =>
        {
            // 編集でホストの装飾が古くなり、行より長い範囲を指していても描画は落ちない。
            var canvas = Canvas("ab");
            var window = WpfTestHost.Load(canvas, Width, Height);
            try
            {
                canvas.SetDiffDecorations(Decorations(
                    new Dictionary<int, DiffDecorationKind> { [0] = DiffDecorationKind.Added, [5] = DiffDecorationKind.Added },
                    new Dictionary<int, IReadOnlyList<DiffInlineRange>>
                    {
                        [0] = [new DiffInlineRange(1, 50)],
                        [5] = [new DiffInlineRange(0, 2)],
                    }));
                Render(canvas);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void Overview_marks_are_placed_on_visual_rows_including_spacers()
    {
        WpfTestHost.Run(() =>
        {
            // 空き行2つ → a, b, c, d。b と c が追加行なら、印は表示行 3〜4。
            var canvas = Canvas("a", "b", "c", "d");
            canvas.SetDiffDecorations(Decorations(
                new Dictionary<int, DiffDecorationKind> { [1] = DiffDecorationKind.Added, [2] = DiffDecorationKind.Added },
                spacers: new Dictionary<int, int> { [0] = 2 }));

            var mark = Assert.Single(canvas.CurrentDiffMarks());
            Assert.Equal(new DiffOverviewMark(3, 2, DiffDecorationKind.Added), mark);
        });
    }

    [Fact]
    public void Spacer_rows_are_marked_with_the_spacer_kind()
    {
        WpfTestHost.Run(() =>
        {
            // 右（新側）のエディタ：左で消えた3行ぶんの空き行が行 1 の前にある。削除だけの変更でも印が出る。
            var canvas = Canvas("a", "b", "c");
            canvas.SetDiffDecorations(new DiffDecorations(
                new Dictionary<int, DiffDecorationKind> { [2] = DiffDecorationKind.Added },
                new Dictionary<int, int> { [1] = 3 })
            { SpacerKind = DiffDecorationKind.Removed });

            Assert.Equal(
                [new DiffOverviewMark(1, 3, DiffDecorationKind.Removed), new DiffOverviewMark(5, 1, DiffDecorationKind.Added)],
                canvas.CurrentDiffMarks());

            // 指定が無ければ空き行に印は付かない（従来どおり）。
            canvas.SetDiffDecorations(new DiffDecorations(
                new Dictionary<int, DiffDecorationKind>(), new Dictionary<int, int> { [1] = 3 }));
            Assert.Empty(canvas.CurrentDiffMarks());
        });
    }

    [Fact]
    public void Overview_marks_follow_new_decorations()
    {
        WpfTestHost.Run(() =>
        {
            var canvas = Canvas("a", "b", "c");
            canvas.SetDiffDecorations(Decorations(new Dictionary<int, DiffDecorationKind> { [0] = DiffDecorationKind.Removed }));
            Assert.Equal(DiffDecorationKind.Removed, Assert.Single(canvas.CurrentDiffMarks()).Kind);

            canvas.SetDiffDecorations(Decorations(new Dictionary<int, DiffDecorationKind> { [2] = DiffDecorationKind.Added }));
            Assert.Equal(new DiffOverviewMark(2, 1, DiffDecorationKind.Added), Assert.Single(canvas.CurrentDiffMarks()));

            canvas.SetDiffDecorations(null);
            Assert.Empty(canvas.CurrentDiffMarks());
        });
    }

    [Fact]
    public void Overview_marks_are_drawn_on_the_scrollbar_track()
    {
        WpfTestHost.Run(() =>
        {
            // 縦スクロールバーが出る行数にして、変更のある高さのトラックが色付くことを確かめる。
            var lines = Enumerable.Range(0, 200).Select(i => $"line {i}").ToArray();
            var canvas = Canvas(lines);
            var window = WpfTestHost.Load(canvas, Width, Height);
            try
            {
                canvas.SetDiffDecorations(DiffDecorations.Empty);
                var plain = Render(canvas);
                canvas.SetDiffDecorations(Decorations(
                    Enumerable.Range(100, 20).ToDictionary(i => i, _ => DiffDecorationKind.Added)));
                var marked = Render(canvas);

                // ウィンドウの枠のぶん、キャンバスは描画先の画像より小さい。トラックはキャンバスの右端。
                var x = (int)canvas.ActualWidth - 3;
                var trackHeight = (int)canvas.ActualHeight;
                var changedRows = Enumerable.Range(0, trackHeight)
                    .Where(y =>
                    {
                        var i = (y * Width + x) * 4;
                        return plain[i] != marked[i] || plain[i + 1] != marked[i + 1] || plain[i + 2] != marked[i + 2];
                    })
                    .ToList();
                Assert.NotEmpty(changedRows);
                // 200 行中 100〜119 行目 → トラックの 50〜60% あたり（最小高さ・サムとの重なりを見込んで広めに）。
                Assert.All(changedRows, y => Assert.InRange(y, trackHeight * 0.4, trackHeight * 0.7));
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void Changes_inside_a_closed_fold_are_marked_on_the_fold_header()
    {
        WpfTestHost.Run(() =>
        {
            // b〜d を畳む（見えるのは a, b, e, f）。畳んだ中の c, d の変更と d の前の空き行は、
            // 消えずに見出し b の表示行（1）に出る。
            var canvas = Canvas("a", "b", "c", "d", "e", "f");
            canvas.SetFolds([0, 1, 4, 5], [1], []);
            canvas.SetDiffDecorations(new DiffDecorations(
                new Dictionary<int, DiffDecorationKind> { [2] = DiffDecorationKind.Added, [5] = DiffDecorationKind.Added },
                new Dictionary<int, int> { [3] = 2 })
            { SpacerKind = DiffDecorationKind.Removed });

            Assert.Equal(
                [new DiffOverviewMark(1, 1, DiffDecorationKind.Added), new DiffOverviewMark(3, 1, DiffDecorationKind.Added)],
                canvas.CurrentDiffMarks());
        });
    }

    /// <summary>
    /// 変更が続く範囲は太い帯になる。印をサムの上に描くと、全行が追加のファイルではトラックが一色に
    /// 潰れて、いまの位置が見えなくなる——印はサムの下。
    /// </summary>
    [Fact]
    public void Overview_marks_are_drawn_under_the_scrollbar_thumb()
    {
        WpfTestHost.Run(() =>
        {
            var lines = Enumerable.Range(0, 200).Select(i => $"line {i}").ToArray();
            var canvas = Canvas(lines);
            canvas.Theme = new Editor.Controls.Themes.EditorTheme { ScrollbarThumb = new SolidColorBrush(Colors.Blue) };
            var window = WpfTestHost.Load(canvas, Width, Height);
            try
            {
                canvas.SetDiffDecorations(Decorations(
                    Enumerable.Range(0, lines.Length).ToDictionary(i => i, _ => DiffDecorationKind.Added)));
                var pixels = Render(canvas);

                // サムは先頭（スクロール 0）。内側 1px 控えて描かれるので、右端から 4px の点はサムの中。
                var x = (int)canvas.ActualWidth - 4;
                var i = (3 * Width + x) * 4;   // Pbgra32: B, G, R, A
                Assert.Equal((byte)255, pixels[i]);
                Assert.Equal((byte)0, pixels[i + 1]);
                Assert.Equal((byte)0, pixels[i + 2]);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void Inline_changes_stop_at_the_minimap()
    {
        WpfTestHost.Run(() =>
        {
            // 行末までの範囲（int.MaxValue）を、画面より長い行に。塗りはミニマップの手前で止まる。
            var text = new string(' ', 200);
            var canvas = Canvas(text);
            canvas.SetMinimap(true);
            var window = WpfTestHost.Load(canvas, Width, Height);
            try
            {
                var lines = new Dictionary<int, DiffDecorationKind> { [0] = DiffDecorationKind.Added };
                canvas.SetDiffDecorations(Decorations(lines));
                var plain = Render(canvas);
                canvas.SetDiffDecorations(Decorations(lines,
                    new Dictionary<int, IReadOnlyList<DiffInlineRange>> { [0] = [new DiffInlineRange(0, int.MaxValue)] }));
                var marked = Render(canvas);

                var changed = ChangedColumns(plain, marked, canvas, row: 0);
                Assert.NotEmpty(changed);
                Assert.All(changed, x => Assert.True(x < canvas.ActualWidth - 80, $"x={x}"));
            }
            finally
            {
                window.Close();
            }
        });
    }
}
