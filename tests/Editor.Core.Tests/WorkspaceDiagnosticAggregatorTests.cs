using System.Text.Json;
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

    [Fact]
    public void CreateResult_UsesLastReportForDuplicateDocumentUris()
    {
        var result = LspWorkspaceDiagnosticAggregator.CreateResult(
        [
            new LspWorkspaceDiagnosticDocument(
                "file:///a.cs",
                1,
                [Diagnostic(0, 0, DiagnosticSeverity.Error, "old")]),
            new LspWorkspaceDiagnosticDocument(
                "file:///A.cs",
                2,
                [Diagnostic(2, 0, DiagnosticSeverity.Warning, "new")]),
        ]);

        Assert.Single(result.Documents);
        Assert.Equal(2, result.Documents[0].Version);
        var diagnostic = Assert.Single(result.Documents[0].Diagnostics);
        Assert.Equal("new", diagnostic.Message);
        Assert.Equal(DiagnosticSeverity.Warning, diagnostic.Severity);
    }

    [Theory]
    [InlineData("""{"diagnosticProvider":true}""", true)]
    [InlineData("""{"diagnosticProvider":false}""", false)]
    [InlineData("""{"diagnosticProvider":{"workspaceDiagnostics":true,"interFileDependencies":true}}""", true)]
    [InlineData("""{"diagnosticProvider":{"workspaceDiagnostics":false,"interFileDependencies":true}}""", false)]
    [InlineData("""{"diagnosticProvider":{"interFileDependencies":true}}""", false)]
    public void SupportsWorkspaceDiagnostics_HandlesBoolAndObjectCapabilities(string json, bool expected)
    {
        using var document = JsonDocument.Parse(json);

        var supported = LspCapabilityParser.SupportsWorkspaceDiagnostics(document.RootElement);

        Assert.Equal(expected, supported);
    }

    [Fact]
    public void ParseWorkspaceDiagnosticResult_ReadsFullReportsAndSkipsUnchangedReports()
    {
        using var document = JsonDocument.Parse("""
            {
              "items": [
                {
                  "kind": "full",
                  "uri": "file:///a.cs",
                  "version": 3,
                  "items": [
                    {
                      "range": {
                        "start": { "line": 4, "character": 2 },
                        "end": { "line": 4, "character": 8 }
                      },
                      "severity": 2,
                      "source": "compiler",
                      "message": "warning text"
                    }
                  ]
                },
                {
                  "kind": "unchanged",
                  "uri": "file:///b.cs",
                  "version": null,
                  "resultId": "previous"
                }
              ]
            }
            """);

        var result = LspWorkspaceDiagnosticParser.Parse(document.RootElement);

        var parsedDocument = Assert.Single(result.Documents);
        Assert.Equal("file:///a.cs", parsedDocument.Uri);
        Assert.Equal(3, parsedDocument.Version);
        var diagnostic = Assert.Single(parsedDocument.Diagnostics);
        Assert.Equal(DiagnosticSeverity.Warning, diagnostic.Severity);
        Assert.Equal("compiler", diagnostic.Source);
        Assert.Equal("warning text", diagnostic.Message);
    }

    private static LspDiagnostic Diagnostic(int line, int character, DiagnosticSeverity severity, string message) =>
        new(
            new LspRange(
                new LspPosition(line, character),
                new LspPosition(line, character + 1)),
            message,
            severity);
}
