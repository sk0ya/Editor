using System.Reflection;
using System.Runtime.CompilerServices;
using System.Windows;
using Editor.Controls.Rendering;

namespace Editor.Controls.Tests;

/// <summary>
/// 注記の印（行に付いた会話の吹き出し）。テストの列の右側に同居するので、
/// 「既定 off で幅 0」「テストの印と押し分けられる」「印の無い行では何も起きない」「全置換」を固定する。
/// </summary>
public class NoteMarkerGutterTests
{
    private static (int Bp, int Bulb, int Test, int Num, double Fold, int Gutter) Metrics(EditorCanvas canvas)
    {
        var method = typeof(EditorCanvas).GetMethod("GetGutterMetrics", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var tuple = (ITuple)method.Invoke(canvas, null)!;
        return ((int)tuple[0]!, (int)tuple[1]!, (int)tuple[2]!, (int)tuple[3]!, (double)tuple[4]!, (int)tuple[5]!);
    }

    private static T Field<T>(EditorCanvas canvas, string name) =>
        (T)typeof(EditorCanvas).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(canvas)!;

    private static double LineHeight(EditorCanvas canvas) => Field<double>(canvas, "_lineHeight");
    private static double ColumnLeft(EditorCanvas canvas)
    {
        var m = Metrics(canvas);
        return Field<double>(canvas, "_blameColWidth") + m.Bp + m.Bulb;
    }

    private static EditorCanvas NewCanvas(int lineCount = 4)
    {
        var canvas = new EditorCanvas();
        canvas.UpdateFont("Consolas", 14);
        canvas.SetLines(Enumerable.Range(0, lineCount).Select(i => $"line {i}").ToArray());
        return canvas;
    }

    private static Point Row(EditorCanvas canvas, int line, double x) => new(x, LineHeight(canvas) * (line + 0.5));

    [Fact]
    public void NoteColumn_DefaultsToDisabled_AndAddsNoWidth()
    {
        WpfTestHost.Run(() =>
        {
            var canvas = NewCanvas();
            var before = Metrics(canvas);
            Assert.Equal(0, before.Test);

            canvas.SetNoteMarkersEnabled(true);
            var after = Metrics(canvas);
            Assert.True(after.Test > 0);
            Assert.Equal(before.Gutter + after.Test, after.Gutter);   // 列のぶんだけガターが広がる

            canvas.SetNoteMarkersEnabled(false);
            Assert.Equal(before, Metrics(canvas));
        });
    }

    [Fact]
    public void NoteClick_ReportsLine_AndLinesWithoutMarkerRaiseNothing()
    {
        WpfTestHost.Run(() =>
        {
            var canvas = NewCanvas();
            canvas.SetNoteMarkersEnabled(true);
            canvas.SetNoteMarkers([new EditorNoteMarker(2, NoteMarkerKind.Open, 1, "レビュー: ここ直して")]);
            var clicks = new List<int>();
            canvas.NoteMarkerClicked += clicks.Add;
            double x = ColumnLeft(canvas) + Metrics(canvas).Test / 2.0;

            Assert.True(canvas.TryClickNoteMarkerColumn(Row(canvas, 2, x)));
            Assert.True(canvas.TryClickNoteMarkerColumn(Row(canvas, 0, x)));   // 列の中なので吸うが、知らせない
            Assert.Equal([2], clicks);
        });
    }

    [Fact]
    public void WithTestGlyphs_LeftIsTestAndRightIsNote()
    {
        WpfTestHost.Run(() =>
        {
            var canvas = NewCanvas();
            canvas.SetTestGlyphsEnabled(true);
            canvas.SetNoteMarkersEnabled(true);
            canvas.SetTestGlyphs([new EditorTestGlyph(1, TestGlyphKind.Run)]);
            canvas.SetNoteMarkers([new EditorNoteMarker(1, NoteMarkerKind.Open)]);
            var tests = new List<int>();
            var notes = new List<int>();
            canvas.TestGlyphClicked += tests.Add;
            canvas.NoteMarkerClicked += notes.Add;
            double left = ColumnLeft(canvas), half = Metrics(canvas).Test / 2.0;

            // 左半分＝テストの印、右半分＝注記の印。同じ行でも押し分けられる。
            Assert.False(canvas.TryClickNoteMarkerColumn(Row(canvas, 1, left + half / 2)));
            Assert.True(canvas.TryClickTestGlyphColumn(Row(canvas, 1, left + half / 2)));
            Assert.False(canvas.TryClickTestGlyphColumn(Row(canvas, 1, left + half * 1.5)));
            Assert.True(canvas.TryClickNoteMarkerColumn(Row(canvas, 1, left + half * 1.5)));
            Assert.Equal([1], tests);
            Assert.Equal([1], notes);
        });
    }

    [Fact]
    public void SetNoteMarkers_ReplacesAll_AndSameContentIsNoOp()
    {
        WpfTestHost.Run(() =>
        {
            var canvas = NewCanvas();
            canvas.SetNoteMarkersEnabled(true);
            canvas.SetNoteMarkers([new EditorNoteMarker(0, NoteMarkerKind.Open), new EditorNoteMarker(3, NoteMarkerKind.Resolved, 2)]);
            Assert.Equal([0, 3], canvas.NoteMarkers.Keys.Order());
            Assert.True(canvas.NoteMarkersUnchanged([new EditorNoteMarker(3, NoteMarkerKind.Resolved, 2), new EditorNoteMarker(0, NoteMarkerKind.Open)]));
            Assert.False(canvas.NoteMarkersUnchanged([new EditorNoteMarker(0, NoteMarkerKind.Resolved)]));

            canvas.SetNoteMarkers([new EditorNoteMarker(1, NoteMarkerKind.Open), new EditorNoteMarker(-1, NoteMarkerKind.Open)]);
            Assert.Equal([1], canvas.NoteMarkers.Keys);   // 負の行は捨てる
            canvas.SetNoteMarkers([]);
            Assert.Empty(canvas.NoteMarkers);
        });
    }
}
