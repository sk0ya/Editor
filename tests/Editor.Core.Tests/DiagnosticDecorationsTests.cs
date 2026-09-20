using Editor.Core.Lsp;

namespace Editor.Core.Tests;

public class DiagnosticDecorationsTests
{
    private static LspDiagnostic Diagnostic(
        int startLine, int startCol, int endLine, int endCol, params DiagnosticTag[] tags) =>
        new(new LspRange(new LspPosition(startLine, startCol), new LspPosition(endLine, endCol)),
            "message", DiagnosticSeverity.Hint, "Roslyn", "IDE0005", null, tags.Length == 0 ? null : tags);

    /// <summary>Roslyn は連続する不要 using を1件の広い範囲で返す。途中の行は行末まで薄字。</summary>
    [Fact]
    public void Multi_line_unnecessary_range_is_clipped_per_line()
    {
        var diagnostics = new[] { Diagnostic(0, 0, 3, 31, DiagnosticTag.Unnecessary) };

        Assert.Equal(
            [new DiagnosticDecoration(0, 20, DiagnosticDecorationKind.Faded)],
            DiagnosticDecorations.ForLine(diagnostics, 0, 20));
        Assert.Equal(
            [new DiagnosticDecoration(0, 18, DiagnosticDecorationKind.Faded)],
            DiagnosticDecorations.ForLine(diagnostics, 1, 18));
        Assert.Equal(
            [new DiagnosticDecoration(0, 31, DiagnosticDecorationKind.Faded)],
            DiagnosticDecorations.ForLine(diagnostics, 3, 40));
    }

    [Fact]
    public void Lines_outside_the_range_get_nothing()
    {
        var diagnostics = new[] { Diagnostic(2, 0, 3, 5, DiagnosticTag.Unnecessary) };
        Assert.Empty(DiagnosticDecorations.ForLine(diagnostics, 1, 10));
        Assert.Empty(DiagnosticDecorations.ForLine(diagnostics, 4, 10));
    }

    [Fact]
    public void Untagged_diagnostics_draw_a_squiggle_and_decorate_nothing()
    {
        var error = new LspDiagnostic(
            new LspRange(new LspPosition(0, 2), new LspPosition(0, 6)),
            "CS0103", DiagnosticSeverity.Error, "Roslyn", "CS0103");

        Assert.True(DiagnosticDecorations.DrawsSquiggle(error));
        Assert.Empty(DiagnosticDecorations.ForLine([error], 0, 10));
    }

    [Fact]
    public void Tagged_diagnostics_replace_the_squiggle()
    {
        Assert.False(DiagnosticDecorations.DrawsSquiggle(Diagnostic(0, 0, 0, 4, DiagnosticTag.Unnecessary)));
        Assert.False(DiagnosticDecorations.DrawsSquiggle(Diagnostic(0, 0, 0, 4, DiagnosticTag.Deprecated)));
    }

    [Fact]
    public void Deprecated_is_struck_through()
    {
        Assert.Equal(
            [new DiagnosticDecoration(2, 3, DiagnosticDecorationKind.StruckThrough)],
            DiagnosticDecorations.ForLine([Diagnostic(1, 2, 1, 5, DiagnosticTag.Deprecated)], 1, 9));
    }

    /// <summary>行末を越える範囲・空の範囲・空行で破綻しない（描画に渡る値なので、ここで潰す）。</summary>
    [Fact]
    public void Out_of_bounds_and_empty_ranges_are_dropped()
    {
        Assert.Equal(
            [new DiagnosticDecoration(1, 4, DiagnosticDecorationKind.Faded)],
            DiagnosticDecorations.ForLine([Diagnostic(0, 1, 0, 99, DiagnosticTag.Unnecessary)], 0, 5));
        Assert.Empty(DiagnosticDecorations.ForLine([Diagnostic(0, 3, 0, 3, DiagnosticTag.Unnecessary)], 0, 5));
        Assert.Empty(DiagnosticDecorations.ForLine([Diagnostic(0, 0, 0, 4, DiagnosticTag.Unnecessary)], 0, 0));
    }
}
