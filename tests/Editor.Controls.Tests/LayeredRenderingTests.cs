using System.Reflection;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Editor.Controls.Rendering;
using Editor.Controls.Themes;
using Editor.Core.Engine;
using Editor.Core.Lsp;
using Editor.Core.Models;
using Editor.Core.Syntax;

namespace Editor.Controls.Tests;

/// <summary>
/// EditorCanvas は本文の絵を行ごとに作り置きし、下層（背景・強調・ガター）／本文層（常駐する行のビジュアル）／
/// 上層（カーソル・波線・ポップアップ…）の3層で描く（EditorCanvas.TextDrawingCache.cs）。
/// <para>作り置きは「入力が同じなら絵も同じ」を前提にしているので、ここでは<b>編集・移動・スクロール・テーマや書体の
/// 変更を経たキャンバス</b>の描画が、<b>同じ最終状態で新しく作ったキャンバス</b>の描画と1画素も違わないことを確かめる
/// （作り置きが古いまま残れば、どこかの画素が食い違う）。重ね順（本文がカーソルや選択に隠れないこと）も確かめる。</para>
/// </summary>
public sealed class LayeredRenderingTests
{
    private const int Width = 640;
    private const int Height = 360;

    private static readonly FieldInfo TextLayerField =
        typeof(EditorCanvas).GetField("_textLayer", BindingFlags.NonPublic | BindingFlags.Instance)!;

    private static string[] CSharpLines(int count) =>
        Enumerable.Range(0, count).Select(i => (i % 7) switch
        {
            0 => $"namespace N{i} {{",
            1 => $"    public class Widget{i} : Base // comment {i}",
            2 => $"    {{ private string _name = \"value {i}\";",
            3 => $"        /* block {i}",
            4 => $"           still comment */ int x{i} = {i} + 0x{i:X};",
            5 => $"        Console.WriteLine($\"{{x{i}}}\"); var 日本語{i} = '字';",
            _ => "    }",
        }).ToArray();

    private static EditorCanvas NewCanvas(double fontSize = 14)
    {
        var canvas = new EditorCanvas();
        canvas.UpdateFont("Consolas", fontSize);
        canvas.IsActive = true;
        return canvas;
    }

    private static void ApplyText(EditorCanvas canvas, SyntaxEngine engine, string[] lines)
    {
        canvas.SetLines(lines);
        canvas.SetTokens(engine.Tokenize(lines));
    }

    private static SyntaxEngine CSharpEngine()
    {
        var engine = new SyntaxEngine();
        engine.SetLanguage("C#");
        return engine;
    }

    private static byte[] Render(EditorCanvas canvas)
    {
        canvas.ResetCursorBlink();
        canvas.UpdateLayout();
        var bitmap = new RenderTargetBitmap(Width, Height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(canvas);
        var pixels = new byte[Width * Height * 4];
        bitmap.CopyPixels(pixels, Width * 4, 0);
        return pixels;
    }

    private static void AssertSamePixels(byte[] expected, byte[] actual, string context)
    {
        int differing = 0, minX = int.MaxValue, minY = int.MaxValue, maxX = -1, maxY = -1, maxDelta = 0;
        for (int i = 0; i < expected.Length; i += 4)
        {
            if (expected[i] != actual[i] || expected[i + 1] != actual[i + 1] || expected[i + 2] != actual[i + 2] || expected[i + 3] != actual[i + 3])
            {
                int x = i / 4 % Width, y = i / 4 / Width;
                minX = Math.Min(minX, x); maxX = Math.Max(maxX, x);
                minY = Math.Min(minY, y); maxY = Math.Max(maxY, y);
                for (int c = 0; c < 3; c++) maxDelta = Math.Max(maxDelta, Math.Abs(expected[i + c] - actual[i + c]));
                differing++;
            }
        }
        if (differing != 0 && Environment.GetEnvironmentVariable("LAYERED_RENDER_DUMP") is { Length: > 0 } dump)
        {
            Save(expected, Path.Combine(dump, context.Replace('/', '_') + "-expected.png"));
            Save(actual, Path.Combine(dump, context.Replace('/', '_') + "-actual.png"));
            var diff = (byte[])actual.Clone();
            for (int i = 0; i < diff.Length; i += 4)
                if (expected[i] != actual[i] || expected[i + 1] != actual[i + 1] || expected[i + 2] != actual[i + 2])
                { diff[i] = 0; diff[i + 1] = 0; diff[i + 2] = 255; diff[i + 3] = 255; }
            Save(diff, Path.Combine(dump, context.Replace('/', '_') + "-diff.png"));
        }
        Assert.True(differing == 0,
            $"{context}: {differing} pixels differ in x={minX}..{maxX}, y={minY}..{maxY}, max channel delta {maxDelta}");
    }

    private static void Save(byte[] pixels, string path)
    {
        var bitmap = BitmapSource.Create(Width, Height, 96, 96, PixelFormats.Pbgra32, null, pixels, Width * 4);
        using var stream = File.Create(path);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        encoder.Save(stream);
    }

    /// <summary>2つのキャンバスを同じ窓の大きさで並べて描き、画素を比べる。</summary>
    private static void CompareWithFresh(EditorCanvas history, Action<EditorCanvas> buildFresh, string context)
    {
        // 新しいキャンバスも先に窓へ載せてから状態を作る（載せる前に置いたカーソルは高さ0の画面に対して
        // スクロール位置を決めてしまい、同じ状態にならない）。
        var fresh = NewCanvas();
        var window = WpfTestHost.Load(fresh, Width, Height);
        try
        {
            buildFresh(fresh);
            fresh.UpdateLayout();
            AssertSamePixels(Render(fresh), Render(history), context);
        }
        finally
        {
            window.Close();
        }
    }

    [Fact]
    public void EditsAndScrolling_RenderSameAsFreshCanvas()
    {
        WpfTestHost.Run(() =>
        {
            var engine = CSharpEngine();
            var lines = CSharpLines(300).ToList();
            var canvas = NewCanvas();
            ApplyText(canvas, engine, lines.ToArray());
            var window = WpfTestHost.Load(canvas, Width, Height);
            try
            {
                Render(canvas);
                canvas.ScrollTo(100 * canvas.LineHeight);
                Render(canvas);

                // 行の挿入（下の行はずれる）・1行の書き換え・削除・コメントを開く編集
                lines.Insert(105, "    int inserted = 1;");
                lines[110] = lines[110] + " // edited";
                lines.RemoveAt(115);
                lines[118] = "    /* opens a comment";
                ApplyText(canvas, engine, lines.ToArray());
                canvas.SetCursor(new CursorPosition(108, 4));
                Render(canvas);
                canvas.ScrollTo(95 * canvas.LineHeight);
                canvas.SetCursor(new CursorPosition(100, 2));
                var finalLines = lines.ToArray();

                CompareWithFresh(canvas, fresh =>
                {
                    ApplyText(fresh, CSharpEngine(), finalLines);
                    fresh.SetCursor(new CursorPosition(100, 2));
                    fresh.ScrollTo(95 * canvas.LineHeight);
                }, "after edits and scrolling");
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void CursorAndModeChanges_RenderSameAsFreshCanvas()
    {
        WpfTestHost.Run(() =>
        {
            var lines = CSharpLines(40);
            var canvas = NewCanvas();
            ApplyText(canvas, CSharpEngine(), lines);
            var window = WpfTestHost.Load(canvas, Width, Height);
            try
            {
                foreach (var (line, column) in new[] { (0, 0), (3, 5), (5, 20), (1, 2) })
                {
                    canvas.SetCursor(new CursorPosition(line, column));
                    Render(canvas);
                }
                canvas.SetMode(VimMode.Insert);
                Render(canvas);
                canvas.SetMode(VimMode.Normal);
                canvas.SetSelection(new Selection(new CursorPosition(2, 4), new CursorPosition(4, 8), SelectionType.Character));
                canvas.SetCursor(new CursorPosition(4, 8));

                CompareWithFresh(canvas, fresh =>
                {
                    ApplyText(fresh, CSharpEngine(), lines);
                    fresh.SetSelection(new Selection(new CursorPosition(2, 4), new CursorPosition(4, 8), SelectionType.Character));
                    fresh.SetCursor(new CursorPosition(4, 8));
                }, "cursor/selection");
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void ThemeAndFontChanges_DoNotReuseStaleDrawings()
    {
        WpfTestHost.Run(() =>
        {
            var lines = CSharpLines(30);
            var canvas = NewCanvas();
            ApplyText(canvas, CSharpEngine(), lines);
            var window = WpfTestHost.Load(canvas, Width, Height);
            try
            {
                Render(canvas);
                canvas.Theme = EditorTheme.Nord;
                canvas.InvalidateVisual();
                Render(canvas);
                canvas.UpdateFont("Consolas", 18);
                Render(canvas);
                canvas.Theme = EditorTheme.OneDark;
                canvas.InvalidateVisual();
                Render(canvas);
                canvas.UpdateFont("Consolas", 16); // 最後は書体だけを変える

                CompareWithFresh(canvas, fresh =>
                {
                    fresh.Theme = EditorTheme.OneDark;
                    fresh.UpdateFont("Consolas", 16);
                    ApplyText(fresh, CSharpEngine(), lines);
                }, "theme/font");
            }
            finally
            {
                window.Close();
            }
        });
    }

    /// <summary>意味トークン・診断の装飾が見た目を変える行（識別子の上に置く）。</summary>
    private static readonly string[] DecoratedLines =
        Enumerable.Range(0, 30).Select(i => $"    int alpha{i} = beta{i} + gamma{i};").ToArray();

    private static LspDiagnostic Faded(int line, int start, int end) =>
        new(new LspRange(new LspPosition(line, start), new LspPosition(line, end)), "unused", DiagnosticSeverity.Hint, Tags: [DiagnosticTag.Unnecessary]);

    private static LspDiagnostic Struck(int line, int start, int end) =>
        new(new LspRange(new LspPosition(line, start), new LspPosition(line, end)), "old", DiagnosticSeverity.Hint, Tags: [DiagnosticTag.Deprecated]);

    [Fact]
    public void SemanticTokenChanges_AreNotHiddenByStaleDrawings()
    {
        WpfTestHost.Run(() =>
        {
            // 識別子 alpha（8〜13桁）に「文字列」の色、beta に readonly（斜体）。どちらも見た目が変わる。
            var semantic = new[]
            {
                new SemanticToken(3, 8, 6, "string", []),
                new SemanticToken(4, 17, 5, "variable", ["readonly"]),
            };
            var canvas = NewCanvas();
            ApplyText(canvas, CSharpEngine(), DecoratedLines);
            var window = WpfTestHost.Load(canvas, Width, Height);
            try
            {
                var plain = Render(canvas);
                canvas.SetSemanticTokens(semantic);
                var colored = Render(canvas);
                Assert.False(plain.SequenceEqual(colored), "test data must make semantic tokens visible");
                canvas.SetSemanticTokens([]);
                Render(canvas);
                canvas.SetSemanticTokens(semantic);

                CompareWithFresh(canvas, fresh =>
                {
                    ApplyText(fresh, CSharpEngine(), DecoratedLines);
                    fresh.SetSemanticTokens(semantic);
                }, "semantic on/off/on");
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void LineShift_ReappliesSemanticTokensAndDiagnosticsByLineNumber()
    {
        WpfTestHost.Run(() =>
        {
            var engine = CSharpEngine();
            var lines = DecoratedLines.ToList();
            var semantic = new[] { new SemanticToken(3, 8, 6, "string", []) };
            var diagnostics = new List<LspDiagnostic> { Faded(5, 8, 14), Struck(6, 17, 22) };
            var canvas = NewCanvas();
            ApplyText(canvas, engine, lines.ToArray());
            canvas.SetSemanticTokens(semantic);
            canvas.SetDiagnostics(diagnostics);
            var window = WpfTestHost.Load(canvas, Width, Height);
            try
            {
                Render(canvas);
                // 行が1つ下へずれた（意味トークンと診断はまだ古い行番号のまま＝その番号の行へ付く）。
                lines.Insert(0, "// shifted");
                ApplyText(canvas, engine, lines.ToArray());
                var finalLines = lines.ToArray();

                CompareWithFresh(canvas, fresh =>
                {
                    ApplyText(fresh, CSharpEngine(), finalLines);
                    fresh.SetSemanticTokens(semantic);
                    fresh.SetDiagnostics(diagnostics);
                }, "line shift");
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void DiagnosticDecorationChanges_AreNotHiddenByStaleDrawings()
    {
        WpfTestHost.Run(() =>
        {
            var canvas = NewCanvas();
            ApplyText(canvas, CSharpEngine(), DecoratedLines);
            var window = WpfTestHost.Load(canvas, Width, Height);
            try
            {
                canvas.SetDiagnostics([Faded(2, 8, 14), Struck(3, 17, 22)]);
                var first = Render(canvas);
                // 行も版も同じまま、装飾だけが別の場所へ移った。
                var moved = new List<LspDiagnostic> { Faded(2, 17, 22), Struck(4, 8, 14) };
                canvas.SetDiagnostics(moved);
                Assert.False(first.SequenceEqual(Render(canvas)), "test data must make the decorations visible");

                CompareWithFresh(canvas, fresh =>
                {
                    ApplyText(fresh, CSharpEngine(), DecoratedLines);
                    fresh.SetDiagnostics(moved);
                }, "diagnostics moved");
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void ThemeSharingForeground_StillRecolorsTokens()
    {
        WpfTestHost.Run(() =>
        {
            // 本文色は同じで、キーワードの色だけが違う2つのテーマ。
            var foreground = new SolidColorBrush(Color.FromRgb(0xEE, 0xEE, 0xEE));
            foreground.Freeze();
            var blue = new EditorTheme { Foreground = foreground, TokenKeyword = Brushes.DeepSkyBlue };
            var red = new EditorTheme { Foreground = foreground, TokenKeyword = Brushes.OrangeRed };
            var canvas = NewCanvas();
            canvas.Theme = blue;
            ApplyText(canvas, CSharpEngine(), DecoratedLines);
            var window = WpfTestHost.Load(canvas, Width, Height);
            try
            {
                Render(canvas);
                canvas.Theme = red;
                canvas.InvalidateVisual();

                CompareWithFresh(canvas, fresh =>
                {
                    fresh.Theme = red;
                    ApplyText(fresh, CSharpEngine(), DecoratedLines);
                }, "keyword color");
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void WrappedSegments_RenderSameRegardlessOfScrollHistory()
    {
        WpfTestHost.Run(() =>
        {
            var lines = Enumerable.Range(0, 60)
                .Select(i => $"line {i}: " + string.Concat(Enumerable.Repeat($"token{i} ", 30)))
                .ToArray();
            var canvas = NewCanvas();
            canvas.WrapLines = true;
            canvas.SetLines(lines);
            var window = WpfTestHost.Load(canvas, Width, Height);
            try
            {
                // 折り返した1行の段が、別々のフレームで初めて画面に入る経緯。
                Render(canvas);
                canvas.ScrollTo(20 * canvas.LineHeight);
                Render(canvas);
                canvas.ScrollTo(7 * canvas.LineHeight);

                CompareWithFresh(canvas, fresh =>
                {
                    fresh.WrapLines = true;
                    fresh.SetLines(lines);
                    fresh.ScrollTo(7 * canvas.LineHeight);
                }, "wrapped history");
            }
            finally
            {
                window.Close();
            }
        });
    }

    private static LspCodeLens Lens(int line, string title) =>
        new(new LspRange(new LspPosition(line, 0), new LspPosition(line, 5)), new LspCodeActionCommand("show.references", title));

    [Fact]
    public void CodeLensLabels_FollowTitleAndStaleChanges()
    {
        WpfTestHost.Run(() =>
        {
            var canvas = NewCanvas();
            ApplyText(canvas, CSharpEngine(), DecoratedLines);
            var window = WpfTestHost.Load(canvas, Width, Height);
            try
            {
                canvas.SetCodeLenses([Lens(2, "3 個の参照"), Lens(2, "1 個の実装"), Lens(5, "0 個の参照")]);
                Render(canvas);
                canvas.UpdateFont("Consolas", 15);
                Render(canvas);
                var lenses = new[] { Lens(2, "12 個の参照"), Lens(2, "1 個の実装"), Lens(5, "7 個の参照") };
                canvas.SetCodeLenses(lenses);
                Render(canvas);
                canvas.SetCodeLensesStale(true); // 最後は色だけが変わる（文字列・書体は同じ）

                CompareWithFresh(canvas, fresh =>
                {
                    fresh.UpdateFont("Consolas", 15);
                    ApplyText(fresh, CSharpEngine(), DecoratedLines);
                    fresh.SetCodeLenses(lenses);
                    fresh.SetCodeLensesStale(true);
                }, "code lens");
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void RelativeLineNumbers_FollowTheCursor()
    {
        WpfTestHost.Run(() =>
        {
            var canvas = NewCanvas();
            ApplyText(canvas, CSharpEngine(), DecoratedLines);
            canvas.ShowRelativeLineNumbers(true);
            var window = WpfTestHost.Load(canvas, Width, Height);
            try
            {
                foreach (var line in new[] { 0, 5, 9, 3 })
                {
                    canvas.SetCursor(new CursorPosition(line, 4));
                    Render(canvas);
                }
                canvas.SetCursor(new CursorPosition(7, 4));

                CompareWithFresh(canvas, fresh =>
                {
                    ApplyText(fresh, CSharpEngine(), DecoratedLines);
                    fresh.ShowRelativeLineNumbers(true);
                    fresh.SetCursor(new CursorPosition(7, 4));
                }, "relative numbers");
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void WrappedAndHorizontallyScrolledText_RenderSameAsFreshCanvas()
    {
        WpfTestHost.Run(() =>
        {
            var lines = Enumerable.Range(0, 60)
                .Select(i => $"line {i}: " + string.Concat(Enumerable.Repeat($"token{i} ", 30)))
                .ToArray();
            var canvas = NewCanvas();
            ApplyText(canvas, CSharpEngine(), lines);
            var window = WpfTestHost.Load(canvas, Width, Height);
            try
            {
                Render(canvas);
                canvas.ScrollTo(10 * canvas.LineHeight, 300);
                Render(canvas);
                canvas.ScrollTo(12 * canvas.LineHeight, 120);

                CompareWithFresh(canvas, fresh =>
                {
                    ApplyText(fresh, CSharpEngine(), lines);
                    fresh.ScrollTo(12 * canvas.LineHeight, 120);
                }, "horizontal scroll");

                canvas.WrapLines = true;
                Render(canvas);
                canvas.ScrollTo(20 * canvas.LineHeight);
                Render(canvas);
                canvas.ScrollTo(7 * canvas.LineHeight);

                CompareWithFresh(canvas, fresh =>
                {
                    fresh.WrapLines = true;
                    ApplyText(fresh, CSharpEngine(), lines);
                    fresh.ScrollTo(7 * canvas.LineHeight);
                }, "wrapped");
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void SameStringInstanceOnTwoLines_IsDrawnOnBoth()
    {
        WpfTestHost.Run(() =>
        {
            // 同じ文字列のインスタンスを貼り重ねた行（p を続けて押したときなど）。字句なしなら作り置きのキーも同じになる。
            string shared = "duplicated line text";
            var lines = new[] { "first", shared, shared, shared, "last" };
            var canvas = NewCanvas();
            canvas.SetLines(lines);
            var window = WpfTestHost.Load(canvas, Width, Height);
            try
            {
                Render(canvas);
                CompareWithFresh(canvas, fresh =>
                    fresh.SetLines(["first", new string(shared), new string(shared), new string(shared), "last"]),
                    "duplicate instances");
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void SpecialLines_TableAlignmentConflictMarkersLongLine_RenderSameAsFresh()
    {
        WpfTestHost.Run(() =>
        {
            var lines = new List<string>
            {
                "| 名前 | 値 |", "| --- | --- |", "| 日本語のセル | 1 |", "| a | 22 |", "",
                "<<<<<<< HEAD", "ours line", "=======", "theirs line", ">>>>>>> branch",
                new string('x', 30_000) + " tail",
            };
            lines.AddRange(Enumerable.Range(0, 30).Select(i => $"after {i}"));
            var canvas = NewCanvas();
            canvas.MarkdownTableAlignEnabled = true;
            canvas.SetLines(lines.ToArray());
            var window = WpfTestHost.Load(canvas, Width, Height);
            try
            {
                Render(canvas);
                canvas.ScrollTo(7 * canvas.LineHeight); // コンフリクトの途中から始まる画面
                Render(canvas);
                canvas.ScrollTo(3 * canvas.LineHeight);
                canvas.SetCursor(new CursorPosition(8, 2));
                var finalLines = lines.ToArray();

                CompareWithFresh(canvas, fresh =>
                {
                    fresh.MarkdownTableAlignEnabled = true;
                    fresh.SetLines(finalLines);
                    fresh.SetCursor(new CursorPosition(8, 2));
                    fresh.ScrollTo(3 * canvas.LineHeight);
                }, "special lines");
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void ConflictRegion_StartingAboveViewport_IsStillHighlighted()
    {
        WpfTestHost.Run(() =>
        {
            var lines = new List<string> { "<<<<<<< HEAD" };
            lines.AddRange(Enumerable.Range(0, 80).Select(i => $"ours {i}"));
            lines.Add("=======");
            lines.Add(">>>>>>> other");
            var withMarker = NewCanvas();
            withMarker.SetLines(lines.ToArray());
            var plain = NewCanvas();
            var plainLines = lines.ToArray();
            plainLines[0] = "-- no marker --";
            plain.SetLines(plainLines);

            var w1 = WpfTestHost.Load(withMarker, Width, Height);
            var w2 = WpfTestHost.Load(plain, Width, Height);
            try
            {
                withMarker.ScrollTo(40 * withMarker.LineHeight);
                plain.ScrollTo(40 * plain.LineHeight);
                var a = Render(withMarker);
                var b = Render(plain);
                // 画面の上端より上で始まったコンフリクトの中なので、見えている行には「こちら側」の背景が付く。
                Assert.False(a.SequenceEqual(b), "the ours-region background must be drawn below an off-screen marker");
            }
            finally
            {
                w1.Close();
                w2.Close();
            }
        });
    }

    [Fact]
    public void TextLayer_KeepsOnlyVisibleRows_AfterScrollingThroughFile()
    {
        WpfTestHost.Run(() =>
        {
            var canvas = NewCanvas();
            ApplyText(canvas, CSharpEngine(), CSharpLines(2000));
            var window = WpfTestHost.Load(canvas, Width, Height);
            try
            {
                for (int top = 0; top < 1900; top += 37)
                {
                    canvas.ScrollTo(top * canvas.LineHeight);
                    canvas.UpdateLayout();
                    Render(canvas);
                }
                var layer = (ContainerVisual)TextLayerField.GetValue(canvas)!;
                int visibleRows = (int)(Height / canvas.LineHeight) + 3;
                Assert.InRange(layer.Children.Count, 1, visibleRows);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void ScrollingVertically_DoesNotRedrawUnchangedRows()
    {
        WpfTestHost.Run(() =>
        {
            var canvas = NewCanvas();
            ApplyText(canvas, CSharpEngine(), CSharpLines(400));
            var window = WpfTestHost.Load(canvas, Width, Height);
            try
            {
                canvas.ScrollTo(50 * canvas.LineHeight);
                Render(canvas);
                var layer = (ContainerVisual)TextLayerField.GetValue(canvas)!;
                static int Renders(Visual row) => (int)row.GetType().GetField("ContentRenders")!.GetValue(row)!;
                var rendersBefore = layer.Children.Cast<Visual>().ToDictionary(row => row, Renders);

                canvas.ScrollTo(51 * canvas.LineHeight);
                Render(canvas);
                canvas.ScrollTo(49 * canvas.LineHeight);
                Render(canvas);

                // 数行ぶんのスクロールで、画面に残った行は同じビジュアルのまま、描き直さずに位置だけが動く。
                var kept = layer.Children.Cast<Visual>().Where(rendersBefore.ContainsKey).ToArray();
                Assert.True(kept.Length >= rendersBefore.Count - 2, $"only {kept.Length} of {rendersBefore.Count} rows were kept");
                Assert.All(kept, row => Assert.Equal(rendersBefore[row], Renders(row)));
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void BlockCursorAndSelection_StayAboveAndBelowText()
    {
        WpfTestHost.Run(() =>
        {
            var canvas = NewCanvas();
            canvas.SetLines(["WWWWWWWWWW", "WWWWWWWWWW", "WWWWWWWWWW"]);
            canvas.SetMode(VimMode.Normal);
            var window = WpfTestHost.Load(canvas, Width, Height);
            try
            {
                // 選択は本文の下：選択した行でも文字は見えている（選択色でない画素がある）。
                canvas.SetCursor(new CursorPosition(2, 9));
                var unselected = Render(canvas);
                canvas.SetSelection(new Selection(new CursorPosition(0, 0), new CursorPosition(0, 9), SelectionType.Character));
                var selected = Render(canvas);
                var rowY = (int)(canvas.LineHeight / 2);
                var textCells = Enumerable.Range(0, Width).Where(x => !SamePixel(selected, unselected, x, rowY)).ToArray();
                Assert.NotEmpty(textCells); // 選択の帯は描かれた
                Assert.Contains(textCells, x => Pixel(selected, x, rowY) != Pixel(selected, textCells[0], rowY)); // 帯の上に文字の画素も残る

                // カーソルは本文の上：ブロックカーソルを載せた桁は、カーソルの無い同じ桁と見た目が変わる。
                canvas.SetSelection(null);
                canvas.SetCursor(new CursorPosition(1, 0));
                var withCursor = Render(canvas);
                canvas.SetCursor(new CursorPosition(2, 9));
                var withoutCursor = Render(canvas);
                var cursorRowY = (int)(canvas.LineHeight * 1.5);
                Assert.Contains(Enumerable.Range(0, 120), x => !SamePixel(withCursor, withoutCursor, x, cursorRowY));
            }
            finally
            {
                window.Close();
            }
        });
    }

    private static uint Pixel(byte[] pixels, int x, int y) => BitConverter.ToUInt32(pixels, (y * Width + x) * 4);

    private static bool SamePixel(byte[] a, byte[] b, int x, int y) => Pixel(a, x, y) == Pixel(b, x, y);

    [Fact]
    public void CursorMoveWithoutScroll_RaisesRevealInPlace_NotScrollChanged()
    {
        WpfTestHost.Run(() =>
        {
            var canvas = NewCanvas();
            canvas.SetLines(CSharpLines(400));
            var window = WpfTestHost.Load(canvas, Width, Height);
            try
            {
                Render(canvas);
                int scrolls = 0, inPlace = 0;
                canvas.ScrollChanged += (_, _) => scrolls++;
                canvas.CursorRevealedInPlace += () => inPlace++;

                canvas.SetCursor(new CursorPosition(3, 0));
                canvas.SetCursor(new CursorPosition(4, 2));
                Assert.Equal(0, scrolls);
                Assert.Equal(2, inPlace);

                canvas.SetCursor(new CursorPosition(300, 0)); // 画面の外へ：スクロールする
                Assert.Equal(1, scrolls);
                Assert.Equal(2, inPlace);
                Assert.True(canvas.VerticalOffset > 0);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void WidthChange_WithWrap_RewrapsLines()
    {
        WpfTestHost.Run(() =>
        {
            var canvas = NewCanvas();
            canvas.WrapLines = true;
            canvas.SetLines([string.Concat(Enumerable.Repeat("word ", 200)), "short"]);
            var window = WpfTestHost.Load(canvas, Width, Height);
            try
            {
                canvas.UpdateLayout();
                double wide = canvas.TotalContentHeight;
                window.Width = Width / 2;
                canvas.UpdateLayout();
                canvas.InvalidateVisual();
                canvas.UpdateLayout();
                double narrow = canvas.TotalContentHeight;
                Assert.True(narrow > wide * 1.5, $"narrow {narrow} vs wide {wide}");

                // 幅が変わらない再描画（カーソル移動）では行レイアウトの結果も変わらない。
                canvas.SetCursor(new CursorPosition(0, 10));
                canvas.UpdateLayout();
                Assert.Equal(narrow, canvas.TotalContentHeight);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void ContentWidth_FollowsTheLongestLineThroughEdits()
    {
        WpfTestHost.Run(() =>
        {
            var lines = Enumerable.Range(0, 500).Select(i => new string('a', i % 90)).ToList();
            var canvas = NewCanvas();
            canvas.SetLines(lines.ToArray());
            var window = WpfTestHost.Load(canvas, Width, Height);
            try
            {
                canvas.UpdateLayout();
                // 等幅（Consolas）なので、横幅＝ガターの幅＋最長の行の文字数×1文字の幅。
                void AssertWidth(int longest, string step)
                {
                    double gutter = GutterWidth(lines.Count);
                    Assert.True(Math.Abs(canvas.TotalContentWidth - (gutter + longest * canvas.CharWidth)) < 0.5,
                        $"{step}: content width {canvas.TotalContentWidth}, expected {gutter + longest * canvas.CharWidth}");
                }
                AssertWidth(89, "initial");

                lines[250] = new string('b', 300);   // 最長の行ができる
                canvas.SetLines(lines.ToArray());
                AssertWidth(300, "longest added");

                lines.Insert(10, "x");               // 上に1行増えて、最長の行が1つ下へずれる
                canvas.SetLines(lines.ToArray());
                AssertWidth(300, "shifted down");

                lines.RemoveRange(0, 5);             // 上が5行減って、5つ上へずれる
                canvas.SetLines(lines.ToArray());
                AssertWidth(300, "shifted up");

                lines[246] = "now short";            // 最長の行が消える
                canvas.SetLines(lines.ToArray());
                AssertWidth(89, "longest removed");

                lines[^1] = new string('c', 120);    // 末尾の行だけが変わる
                canvas.SetLines(lines.ToArray());
                AssertWidth(120, "last line");
            }
            finally
            {
                window.Close();
            }

            static double GutterWidth(int lineCount)
            {
                var empty = NewCanvas();
                empty.SetLines(Enumerable.Repeat("", lineCount).ToArray());
                var w = WpfTestHost.Load(empty, Width, Height);
                try
                {
                    empty.UpdateLayout();
                    return empty.TotalContentWidth;
                }
                finally
                {
                    w.Close();
                }
            }
        });
    }

    [Fact]
    public void SetTokens_SparseAndUnsortedTokens_AreLookedUpByLine()
    {
        WpfTestHost.Run(() =>
        {
            var canvas = NewCanvas();
            canvas.SetLines(Enumerable.Range(0, 10).Select(i => $"word{i} other").ToArray());
            var tokensForLine = typeof(EditorCanvas).GetMethod("TokensForLine", BindingFlags.NonPublic | BindingFlags.Instance)!;
            SyntaxToken[]? At(int line) => (SyntaxToken[]?)tokensForLine.Invoke(canvas, [line]);

            var unsorted = new[] { new SyntaxToken(6, 5, TokenKind.Identifier), new SyntaxToken(0, 5, TokenKind.Keyword) };
            canvas.SetTokens([new LineTokens(3, unsorted), new LineTokens(7, [new SyntaxToken(0, 4, TokenKind.Type)])]);

            Assert.Null(At(0));
            Assert.Equal([0, 6], At(3)!.Select(t => t.StartColumn));
            Assert.Equal(TokenKind.Type, Assert.Single(At(7)!).Kind);
            Assert.Null(At(42));

            var dense = Enumerable.Range(0, 10).Select(i => new LineTokens(i, [new SyntaxToken(0, 4, TokenKind.Keyword)])).ToArray();
            canvas.SetTokens(dense);
            Assert.Same(dense[5].Tokens, At(5));
            Assert.Null(At(10));
        });
    }
}
