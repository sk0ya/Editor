using System.Text.Json;

namespace Editor.Core.Lsp;

public record LspPosition(int Line, int Character);
public record LspRange(LspPosition Start, LspPosition End);

public enum DiagnosticSeverity { Error = 1, Warning = 2, Information = 3, Hint = 4 }

public record LspDiagnostic(
    LspRange Range,
    string Message,
    DiagnosticSeverity Severity,
    string? Source = null,
    string? Code = null);

/// <summary>textDocument/diagnostic の応答1件。
/// <paramref name="Unchanged"/> が true のときサーバーは「前回と同じ」とだけ答えているので、
/// <paramref name="Diagnostics"/>（空）で上書きせず既存の診断を維持すること。</summary>
public record LspDocumentDiagnosticReport(
    IReadOnlyList<LspDiagnostic> Diagnostics,
    string? ResultId,
    bool Unchanged);

public record LspWorkspaceDiagnosticDocument(
    string Uri,
    int? Version,
    IReadOnlyList<LspDiagnostic> Diagnostics);

public record LspWorkspaceDiagnosticSummary(
    int DocumentCount,
    int DiagnosticCount,
    int ErrorCount,
    int WarningCount,
    int InformationCount,
    int HintCount);

public record LspWorkspaceDiagnosticResult(
    IReadOnlyList<LspWorkspaceDiagnosticDocument> Documents,
    LspWorkspaceDiagnosticSummary Summary);

public static class LspWorkspaceDiagnosticAggregator
{
    public static LspWorkspaceDiagnosticResult CreateResult(IEnumerable<LspWorkspaceDiagnosticDocument> documents)
    {
        var ordered = documents
            .Where(d => !string.IsNullOrWhiteSpace(d.Uri))
            .GroupBy(d => d.Uri, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.Last())
            .OrderBy(d => d.Uri, StringComparer.OrdinalIgnoreCase)
            .Select(d => d with
            {
                Diagnostics = d.Diagnostics
                    .OrderBy(x => x.Range.Start.Line)
                    .ThenBy(x => x.Range.Start.Character)
                    .ToArray()
            })
            .ToArray();

        var summary = new LspWorkspaceDiagnosticSummary(
            ordered.Length,
            ordered.Sum(d => d.Diagnostics.Count),
            CountSeverity(ordered, DiagnosticSeverity.Error),
            CountSeverity(ordered, DiagnosticSeverity.Warning),
            CountSeverity(ordered, DiagnosticSeverity.Information),
            CountSeverity(ordered, DiagnosticSeverity.Hint));

        return new LspWorkspaceDiagnosticResult(ordered, summary);
    }

    private static int CountSeverity(
        IReadOnlyList<LspWorkspaceDiagnosticDocument> documents,
        DiagnosticSeverity severity) =>
        documents.Sum(d => d.Diagnostics.Count(x => x.Severity == severity));
}

public static class LspCapabilityParser
{
    public static bool SupportsDocumentDiagnostics(JsonElement capabilities)
    {
        if (capabilities.ValueKind != JsonValueKind.Object ||
            !capabilities.TryGetProperty("diagnosticProvider", out var diagnosticProvider))
            return false;

        return diagnosticProvider.ValueKind is JsonValueKind.True or JsonValueKind.Object;
    }

    /// <summary>サーバーが宣言した <c>diagnosticProvider.identifier</c>。
    /// 宣言されている場合、クライアントは textDocument/diagnostic でこれを送り返す必要がある
    /// （識別子ごとに診断を分割するサーバーがあるため）。</summary>
    public static string? DocumentDiagnosticIdentifier(JsonElement capabilities)
    {
        if (capabilities.ValueKind != JsonValueKind.Object ||
            !capabilities.TryGetProperty("diagnosticProvider", out var diagnosticProvider) ||
            diagnosticProvider.ValueKind != JsonValueKind.Object ||
            !diagnosticProvider.TryGetProperty("identifier", out var identifier) ||
            identifier.ValueKind != JsonValueKind.String)
            return null;

        var value = identifier.GetString();
        return string.IsNullOrEmpty(value) ? null : value;
    }

    public static bool SupportsWorkspaceDiagnostics(JsonElement capabilities)
    {
        if (capabilities.ValueKind != JsonValueKind.Object ||
            !capabilities.TryGetProperty("diagnosticProvider", out var diagnosticProvider))
            return false;

        return diagnosticProvider.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.Object when diagnosticProvider.TryGetProperty("workspaceDiagnostics", out var workspaceDiagnostics) =>
                workspaceDiagnostics.ValueKind == JsonValueKind.True,
            _ => false
        };
    }
}

public static class LspDocumentDiagnosticParser
{
    /// <summary>textDocument/diagnostic の result を解析する。
    /// 診断レポートとして読めない形（object でない / items が無い full レポート）なら null。
    /// 「診断ゼロ件」は空リストを持つレポートであって null ではない。</summary>
    public static LspDocumentDiagnosticReport? Parse(JsonElement result)
    {
        if (result.ValueKind != JsonValueKind.Object) return null;

        var resultId = result.TryGetProperty("resultId", out var resultIdEl) &&
                       resultIdEl.ValueKind == JsonValueKind.String
            ? resultIdEl.GetString()
            : null;

        if (result.TryGetProperty("kind", out var kindEl) &&
            kindEl.ValueKind == JsonValueKind.String &&
            string.Equals(kindEl.GetString(), "unchanged", StringComparison.OrdinalIgnoreCase))
            return new LspDocumentDiagnosticReport([], resultId, Unchanged: true);

        if (!result.TryGetProperty("items", out var itemsEl) || itemsEl.ValueKind != JsonValueKind.Array)
            return null;

        var diagnostics = new List<LspDiagnostic>();
        foreach (var item in itemsEl.EnumerateArray())
            if (LspWorkspaceDiagnosticParser.TryParseDiagnostic(item, out var diagnostic))
                diagnostics.Add(diagnostic);
        return new LspDocumentDiagnosticReport(diagnostics, resultId, Unchanged: false);
    }
}

public static class LspWorkspaceDiagnosticParser
{
    public static LspWorkspaceDiagnosticResult Parse(JsonElement result)
    {
        if (result.ValueKind != JsonValueKind.Object ||
            !result.TryGetProperty("items", out var itemsEl) ||
            itemsEl.ValueKind != JsonValueKind.Array)
            return LspWorkspaceDiagnosticAggregator.CreateResult([]);

        var documents = new List<LspWorkspaceDiagnosticDocument>();
        foreach (var item in itemsEl.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object ||
                !item.TryGetProperty("uri", out var uriEl))
                continue;

            var uri = LspUri.Normalize(uriEl.GetString() ?? "");
            if (string.IsNullOrWhiteSpace(uri))
                continue;

            if (item.TryGetProperty("kind", out var kindEl) &&
                kindEl.ValueKind == JsonValueKind.String &&
                string.Equals(kindEl.GetString(), "unchanged", StringComparison.OrdinalIgnoreCase))
                continue;

            if (!item.TryGetProperty("items", out var diagnosticsEl) ||
                diagnosticsEl.ValueKind != JsonValueKind.Array)
                continue;

            int? version = item.TryGetProperty("version", out var versionEl) &&
                versionEl.ValueKind == JsonValueKind.Number
                    ? versionEl.GetInt32()
                    : null;

            var diagnostics = new List<LspDiagnostic>();
            foreach (var diagnosticEl in diagnosticsEl.EnumerateArray())
            {
                if (TryParseDiagnostic(diagnosticEl, out var diagnostic))
                    diagnostics.Add(diagnostic);
            }

            documents.Add(new LspWorkspaceDiagnosticDocument(uri, version, diagnostics));
        }

        return LspWorkspaceDiagnosticAggregator.CreateResult(documents);
    }

    internal static bool TryParseDiagnostic(JsonElement el, out LspDiagnostic diagnostic)
    {
        diagnostic = new LspDiagnostic(
            new LspRange(new LspPosition(0, 0), new LspPosition(0, 0)),
            "",
            DiagnosticSeverity.Error);

        try
        {
            if (!el.TryGetProperty("range", out var rangeEl))
                return false;

            var range = ParseRange(rangeEl);
            var message = el.TryGetProperty("message", out var messageEl) ? messageEl.GetString() ?? "" : "";
            var severity = el.TryGetProperty("severity", out var severityEl) &&
                severityEl.ValueKind == JsonValueKind.Number
                    ? (DiagnosticSeverity)severityEl.GetInt32()
                    : DiagnosticSeverity.Error;
            var source = el.TryGetProperty("source", out var sourceEl) ? sourceEl.GetString() : null;
            diagnostic = new LspDiagnostic(range, message, severity, source);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static LspRange ParseRange(JsonElement el)
    {
        var start = el.GetProperty("start");
        var end = el.GetProperty("end");
        return new LspRange(
            new LspPosition(start.GetProperty("line").GetInt32(), start.GetProperty("character").GetInt32()),
            new LspPosition(end.GetProperty("line").GetInt32(), end.GetProperty("character").GetInt32()));
    }
}

public enum CompletionItemKind
{
    Text = 1, Method = 2, Function = 3, Constructor = 4, Field = 5,
    Variable = 6, Class = 7, Interface = 8, Module = 9, Property = 10,
    Unit = 11, Value = 12, Enum = 13, Keyword = 14, Snippet = 15,
    Color = 16, File = 17, Reference = 18
}

/// <summary>LSP insertTextFormat: 1 = PlainText, 2 = Snippet</summary>
public enum InsertTextFormat { PlainText = 1, Snippet = 2 }

public record LspCompletionItem(
    string Label,
    CompletionItemKind Kind = CompletionItemKind.Text,
    string? Detail = null,
    string? InsertText = null,
    string? FilterText = null,
    string? Documentation = null,
    InsertTextFormat TextFormat = InsertTextFormat.PlainText,
    string? SortText = null,
    bool Preselect = false,
    LspTextEdit? TextEdit = null,
    IReadOnlyList<string>? CommitCharacters = null,
    bool Deprecated = false,
    IReadOnlyList<LspTextEdit>? AdditionalTextEdits = null,
    string? DataJson = null,
    LspCompletionCommand? Command = null,
    string? RawJson = null);

/// <summary>補完採用時またはresolve後に実行できるLSP command。引数はJSON文字列で保持する。</summary>
public record LspCompletionCommand(string Title, string Command, IReadOnlyList<string>? ArgumentsJson = null);

public record LspHover(string Value);

// Signature Help
public record LspParameterInfo(string? Label);
public record LspSignatureInfo(string Label, string? Documentation, IReadOnlyList<LspParameterInfo> Parameters);
public record LspSignatureHelp(IReadOnlyList<LspSignatureInfo> Signatures, int ActiveSignature, int ActiveParameter);

// Text edits (for formatting / rename)
public record LspTextEdit(LspRange Range, string NewText);

// Location (for find references)
public record LspLocation(string Uri, LspRange Range);

/// <summary>workspace edit に含まれるファイル操作の種類（<c>documentChanges</c> の
/// <c>CreateFile</c>／<c>RenameFile</c>／<c>DeleteFile</c>）。</summary>
public enum LspFileOperationKind { Create, Rename, Delete }

/// <summary>workspace edit のファイル操作1件。「クラスに抽出」「型をファイルへ移動」等の
/// リファクタリングは、本文の編集だけでなく**ファイルの新規作成**を伴うので、これを落とすと
/// 「適用したのに何も起きない」になる。<paramref name="NewUri"/> は Rename のときだけ意味を持つ。</summary>
public record LspFileOperation(
    LspFileOperationKind Kind,
    string Uri,
    string? NewUri = null,
    bool Overwrite = false,
    bool IgnoreIfExists = false,
    bool Recursive = false,
    bool IgnoreIfNotExists = false);

// Workspace edit (for rename — maps file URI → list of edits)
public record LspWorkspaceEdit(
    IReadOnlyDictionary<string, IReadOnlyList<LspTextEdit>> Changes,
    IReadOnlyDictionary<string, int?>? DocumentVersions = null,
    IReadOnlyList<LspFileOperation>? FileOperations = null,
    // Optional host-side guard for locally generated edits. Server JSON never supplies this.
    IReadOnlyDictionary<string, string>? ExpectedTexts = null);

// Folding ranges
public record LspFoldingRange(int StartLine, int EndLine, string? Kind = null);

// Code actions
/// <summary>code action が持つ <c>command</c>。サーバー側で実行し、結果は
/// サーバー起点の <c>workspace/applyEdit</c> で返ってくる（tsserver 系の抽出リファクタリングがこの形）。
/// 引数は JSON のまま保持する——<see cref="System.Text.Json.JsonElement"/> は
/// 元の <c>JsonDocument</c> が破棄されると読めなくなるため、Core へは持ち込まない。</summary>
public record LspCodeActionCommand(
    string Command,
    string? Title = null,
    IReadOnlyList<string>? ArgumentsJson = null);

/// <summary>textDocument/codeLens の1件。未解決の場合は codeLens/resolve 用の元JSONを保持する。</summary>
public record LspCodeLens(
    LspRange Range,
    LspCodeActionCommand? Command = null,
    string? DataJson = null,
    string? RawJson = null)
{
    public bool NeedsResolve => Command is null && RawJson is not null;
    public string Title => Command?.Title ?? "CodeLens";
}

public static class LspCodeLensParser
{
    public static IReadOnlyList<LspCodeLens> Parse(JsonElement result)
    {
        if (result.ValueKind != JsonValueKind.Array) return [];

        var lenses = new List<LspCodeLens>();
        foreach (var item in result.EnumerateArray())
        {
            try
            {
                if (!item.TryGetProperty("range", out var range)) continue;
                LspCodeActionCommand? command = null;
                if (item.TryGetProperty("command", out var commandEl))
                    command = ParseCommand(commandEl);
                string? data = item.TryGetProperty("data", out var dataEl)
                    ? dataEl.GetRawText()
                    : null;
                lenses.Add(new LspCodeLens(ParseRange(range), command, data, item.GetRawText()));
            }
            catch
            {
                // A malformed lens must not hide valid lenses next to it.
            }
            if (lenses.Count >= 500) break;
        }
        return lenses;
    }

    private static LspCodeActionCommand? ParseCommand(JsonElement el)
    {
        if (el.ValueKind != JsonValueKind.Object ||
            !el.TryGetProperty("command", out var commandEl) ||
            commandEl.ValueKind != JsonValueKind.String ||
            commandEl.GetString() is not { Length: > 0 } command)
            return null;

        string? title = el.TryGetProperty("title", out var titleEl) ? titleEl.GetString() : null;
        List<string>? arguments = null;
        if (el.TryGetProperty("arguments", out var argumentsEl) && argumentsEl.ValueKind == JsonValueKind.Array)
        {
            arguments = [];
            foreach (var argument in argumentsEl.EnumerateArray())
                arguments.Add(argument.GetRawText());
        }
        return new LspCodeActionCommand(command, title, arguments);
    }

    private static LspRange ParseRange(JsonElement el)
    {
        var start = el.GetProperty("start");
        var end = el.GetProperty("end");
        return new LspRange(
            new LspPosition(start.GetProperty("line").GetInt32(), start.GetProperty("character").GetInt32()),
            new LspPosition(end.GetProperty("line").GetInt32(), end.GetProperty("character").GetInt32()));
    }
}

/// <summary>code action 1件。
/// <para><paramref name="Edit"/> が null でも「編集が無い」とは限らない。LSP は遅延解決を許しており、
/// Roslyn は一覧では title と data だけを返して <c>codeAction/resolve</c> で初めて edit を作る。
/// したがって適用時は必ず解決（<see cref="ILspClient.ResolveCodeActionAsync"/>）を挟むこと。</para>
/// <paramref name="RawJson"/> はサーバーが返した元の JSON をそのまま保持したもので、解決要求に
/// そのまま送り返すために必要（<c>data</c> の中身はサーバー固有で、こちらで解釈してはいけない）。</summary>
public record LspCodeAction(
    string Title,
    string? Kind,
    LspWorkspaceEdit? Edit,
    LspCodeActionCommand? Command = null,
    string? RawJson = null,
    bool IsPreferred = false,
    string? DisabledReason = null)
{
    /// <summary>解決すれば編集が得られる可能性があるか。edit も command も無く data だけを持つ
    /// 未解決アクションが該当する。</summary>
    public bool NeedsResolve => Edit is null && Command is null && RawJson is not null;
}

/// <summary>LSP 標準の code action kind。<c>only</c> フィルタと分類の両方で使う。
/// kind は「<c>refactor.extract.function</c> は <c>refactor.extract</c> の下位」という
/// **ドット区切りの階層**なので、比較は前方一致（<see cref="Matches"/>）で行う。</summary>
public static class LspCodeActionKinds
{
    public const string QuickFix = "quickfix";
    public const string Refactor = "refactor";
    public const string RefactorExtract = "refactor.extract";
    public const string RefactorInline = "refactor.inline";
    public const string RefactorRewrite = "refactor.rewrite";
    public const string RefactorMove = "refactor.move";
    public const string Source = "source";
    public const string SourceFixAll = "source.fixAll";

    /// <summary><paramref name="kind"/> が <paramref name="prefix"/> と等しいか、その下位階層か。</summary>
    public static bool Matches(string? kind, string prefix) =>
        kind is not null &&
        (string.Equals(kind, prefix, StringComparison.Ordinal) ||
         kind.StartsWith(prefix + ".", StringComparison.Ordinal));
}

// Inlay hints
public enum InlayHintKind { Type = 1, Parameter = 2 }
public record InlayHint(LspPosition Position, string Label, InlayHintKind Kind);

// Document symbols (hierarchical, returned by textDocument/documentSymbol)
public record DocumentSymbol(
    string Name,
    SymbolKind Kind,
    LspRange Range,
    LspRange SelectionRange,
    DocumentSymbol[]? Children);

// Call hierarchy
public record CallHierarchyItem(
    string Name,
    int Kind,
    string Uri,
    LspRange Range,
    LspRange SelectionRange,
    JsonElement? Data = null);
public record CallHierarchyIncomingCall(CallHierarchyItem From, LspRange[] FromRanges);
public record CallHierarchyOutgoingCall(CallHierarchyItem To, LspRange[] FromRanges);

// Type hierarchy
public record TypeHierarchyItem(
    string Name,
    int Kind,
    string Uri,
    LspRange Range,
    LspRange SelectionRange,
    JsonElement? Data = null);

// Semantic tokens
public record SemanticTokensLegend(string[] TokenTypes, string[] TokenModifiers);
public record SemanticToken(int Line, int StartChar, int Length, string TokenType, string[] Modifiers);
/// <summary>Decoded semantic tokens together with the server's result id.
/// The id is used by <c>textDocument/semanticTokens/full/delta</c> on the next refresh.</summary>
public record SemanticTokensResult(string? ResultId, SemanticToken[] Tokens);

// Document highlight
public enum DocumentHighlightKind { Text = 1, Read = 2, Write = 3 }
public record DocumentHighlight(LspRange Range, DocumentHighlightKind Kind);

/// <summary>textDocument/documentLink の1件。Target は file:// または http(s) URI。</summary>
public record LspDocumentLink(LspRange Range, string Target, string? Tooltip = null);

/// <summary>documentLink 応答を安全に読み取る。壊れた要素は捨て、正常なリンクは残す。</summary>
public static class LspDocumentLinkParser
{
    public static IReadOnlyList<LspDocumentLink> Parse(JsonElement result)
    {
        if (result.ValueKind != JsonValueKind.Array) return [];

        var links = new List<LspDocumentLink>();
        foreach (var item in result.EnumerateArray())
        {
            try
            {
                if (!item.TryGetProperty("range", out var range) ||
                    !item.TryGetProperty("target", out var target) ||
                    target.ValueKind != JsonValueKind.String)
                    continue;

                var targetText = target.GetString();
                if (string.IsNullOrWhiteSpace(targetText)) continue;

                string? tooltip = item.TryGetProperty("tooltip", out var tooltipEl) &&
                    tooltipEl.ValueKind == JsonValueKind.String
                    ? tooltipEl.GetString()
                    : null;
                links.Add(new LspDocumentLink(ParseRange(range), LspUri.Normalize(targetText), tooltip));
            }
            catch
            {
                // One malformed link must not hide valid links returned beside it.
            }
            if (links.Count >= 1000) break;
        }
        return links;
    }

    private static LspRange ParseRange(JsonElement el)
    {
        var start = el.GetProperty("start");
        var end = el.GetProperty("end");
        return new LspRange(
            new LspPosition(start.GetProperty("line").GetInt32(), start.GetProperty("character").GetInt32()),
            new LspPosition(end.GetProperty("line").GetInt32(), end.GetProperty("character").GetInt32()));
    }
}

// Selection ranges (returned by textDocument/selectionRange)
public record LspSelectionRange(LspRange Range, LspSelectionRange? Parent = null);

// Workspace symbols
public enum SymbolKind
{
    File = 1, Module = 2, Namespace = 3, Package = 4,
    Class = 5, Method = 6, Property = 7, Field = 8,
    Constructor = 9, Enum = 10, Interface = 11, Function = 12,
    Variable = 13, Constant = 14, String = 15, Number = 16,
    Boolean = 17, Array = 18, Object = 19, Key = 20,
    Null = 21, EnumMember = 22, Struct = 23, Event = 24,
    Operator = 25, TypeParameter = 26
}

public record LspSymbolInformation(
    string Name,
    SymbolKind Kind,
    LspLocation Location,
    string? ContainerName = null);

// One clickable element of the breadcrumb bar: a symbol along the path from the
// document root to the cursor, plus the position to jump to when clicked.
public record BreadcrumbSegment(string Name, SymbolKind Kind, int Line, int Column);
