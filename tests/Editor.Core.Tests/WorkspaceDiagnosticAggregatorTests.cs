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

    [Theory]
    [InlineData("""{"diagnosticProvider":true}""", true)]
    [InlineData("""{"diagnosticProvider":false}""", false)]
    [InlineData("""{"diagnosticProvider":{"workspaceDiagnostics":false}}""", true)]
    [InlineData("""{}""", false)]
    public void SupportsDocumentDiagnostics_HandlesBoolAndObjectCapabilities(string json, bool expected)
    {
        using var document = JsonDocument.Parse(json);

        Assert.Equal(expected, LspCapabilityParser.SupportsDocumentDiagnostics(document.RootElement));
    }

    [Fact]
    public void ParseDocumentDiagnosticResult_ReadsFullReport()
    {
        using var document = JsonDocument.Parse("""
            {
              "kind": "full",
              "items": [{
                "range": {
                  "start": { "line": 2, "character": 1 },
                  "end": { "line": 2, "character": 4 }
                },
                "severity": 1,
                "source": "compiler",
                "message": "broken"
              }]
            }
            """);

        var report = LspDocumentDiagnosticParser.Parse(document.RootElement);

        Assert.NotNull(report);
        Assert.False(report.Unchanged);
        var diagnostic = Assert.Single(report.Diagnostics);
        Assert.Equal("broken", diagnostic.Message);
        Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity);
    }

    [Fact]
    public void ParseDocumentDiagnosticResult_ReadsResultId()
    {
        using var document = JsonDocument.Parse("""{"kind":"full","resultId":"7","items":[]}""");

        var report = LspDocumentDiagnosticParser.Parse(document.RootElement);

        Assert.NotNull(report);
        Assert.False(report.Unchanged);
        Assert.Empty(report.Diagnostics);
        Assert.Equal("7", report.ResultId);
    }

    [Fact]
    public void ParseDocumentDiagnosticResult_FlagsUnchangedReport()
    {
        using var document = JsonDocument.Parse("""{"kind":"unchanged","resultId":"2"}""");

        var report = LspDocumentDiagnosticParser.Parse(document.RootElement);

        Assert.NotNull(report);
        Assert.True(report.Unchanged);
        Assert.Empty(report.Diagnostics);
        Assert.Equal("2", report.ResultId);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("""{"kind":"full"}""")]
    public void ParseDocumentDiagnosticResult_ReturnsNullForUnreadableResult(string json)
    {
        using var document = JsonDocument.Parse(json);

        Assert.Null(LspDocumentDiagnosticParser.Parse(document.RootElement));
    }

    [Theory]
    [InlineData("""{"diagnosticProvider":{"identifier":"roslyn"}}""", "roslyn")]
    [InlineData("""{"diagnosticProvider":{"identifier":""}}""", null)]
    [InlineData("""{"diagnosticProvider":{}}""", null)]
    [InlineData("""{"diagnosticProvider":true}""", null)]
    [InlineData("""{}""", null)]
    public void DocumentDiagnosticIdentifier_ReadsProviderIdentifier(string json, string? expected)
    {
        using var document = JsonDocument.Parse(json);

        Assert.Equal(expected, LspCapabilityParser.DocumentDiagnosticIdentifier(document.RootElement));
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

    // 診断の URI はホスト側で「どのタブの診断か」を引くキーになる。tsserver 系の
    // "file:///c%3A/…" のままだと引けないので、パーサの時点で正規化する。
    [Fact]
    public void ParseWorkspaceDiagnosticResult_NormalizesPercentEncodedDriveUris()
    {
        using var document = JsonDocument.Parse("""
            {
              "items": [{
                "kind": "full",
                "uri": "file:///c%3A/work/a.ts",
                "items": [{
                  "range": { "start": { "line": 0, "character": 0 }, "end": { "line": 0, "character": 1 } },
                  "severity": 1,
                  "message": "broken"
                }]
              }]
            }
            """);

        var result = LspWorkspaceDiagnosticParser.Parse(document.RootElement);

        var parsedDocument = Assert.Single(result.Documents);
        Assert.True(LspUri.MatchesPath(parsedDocument.Uri, @"C:\work\a.ts"));
    }

    private static LspDiagnostic Diagnostic(int line, int character, DiagnosticSeverity severity, string message) =>
        new(
            new LspRange(
                new LspPosition(line, character),
                new LspPosition(line, character + 1)),
            message,
            severity);
}
