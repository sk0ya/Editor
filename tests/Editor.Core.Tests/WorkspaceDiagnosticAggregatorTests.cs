using Editor.Core.Lsp;

namespace Editor.Core.Tests;

public class WorkspaceDiagnosticAggregatorTests
{
    [Fact]
    public void CreateResult_SortsDocumentsAndDiagnostics()
    {
        var result = LspWorkspaceDiagnosticAggregator.CreateResult(
        [
            new LspWorkspaceDiagnosticDocument(
                "file:///b.cs",
                null,
                [
                    Diagnostic(4, 2, DiagnosticSeverity.Warning, "late"),
                    Diagnostic(1, 7, DiagnosticSeverity.Error, "early"),
                ]),
            new LspWorkspaceDiagnosticDocument(
                "file:///a.cs",
                3,
                [
                    Diagnostic(9, 1, DiagnosticSeverity.Hint, "hint"),
                ]),
        ]);

        Assert.Equal(["file:///a.cs", "file:///b.cs"], result.Documents.Select(d => d.Uri));
        Assert.Equal([1, 4], result.Documents[1].Diagnostics.Select(d => d.Range.Start.Line));
    }

    [Fact]
    public void CreateResult_CountsDiagnosticSeverities()
    {
        var result = LspWorkspaceDiagnosticAggregator.CreateResult(
        [
            new LspWorkspaceDiagnosticDocument(
                "file:///a.cs",
                null,
                [
                    Diagnostic(0, 0, DiagnosticSeverity.Error, "error"),
                    Diagnostic(1, 0, DiagnosticSeverity.Warning, "warning"),
                    Diagnostic(2, 0, DiagnosticSeverity.Information, "info"),
                ]),
            new LspWorkspaceDiagnosticDocument(
                "file:///b.cs",
                null,
                [
                    Diagnostic(0, 0, DiagnosticSeverity.Error, "error 2"),
                    Diagnostic(1, 0, DiagnosticSeverity.Hint, "hint"),
                ]),
        ]);

        Assert.Equal(2, result.Summary.DocumentCount);
        Assert.Equal(5, result.Summary.DiagnosticCount);
        Assert.Equal(2, result.Summary.ErrorCount);
        Assert.Equal(1, result.Summary.WarningCount);
        Assert.Equal(1, result.Summary.InformationCount);
        Assert.Equal(1, result.Summary.HintCount);
    }

    [Fact]
    public void CreateResult_IgnoresBlankDocumentUris()
    {
        var result = LspWorkspaceDiagnosticAggregator.CreateResult(
        [
            new LspWorkspaceDiagnosticDocument("", null, [Diagnostic(0, 0, DiagnosticSeverity.Error, "ignored")]),
            new LspWorkspaceDiagnosticDocument("file:///a.cs", null, []),
        ]);

        Assert.Single(result.Documents);
        Assert.Equal(0, result.Summary.DiagnosticCount);
    }

    private static LspDiagnostic Diagnostic(int line, int character, DiagnosticSeverity severity, string message) =>
        new(
            new LspRange(
                new LspPosition(line, character),
                new LspPosition(line, character + 1)),
            message,
            severity);
}
