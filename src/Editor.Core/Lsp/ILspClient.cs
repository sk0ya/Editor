namespace Editor.Core.Lsp;

public class DiagnosticsChangedEventArgs(string uri, IReadOnlyList<LspDiagnostic> diagnostics) : EventArgs
{
    public string Uri { get; } = uri;
    public IReadOnlyList<LspDiagnostic> Diagnostics { get; } = diagnostics;
}

public interface ILspClient : IDisposable
{
    bool IsRunning { get; }
    /// <summary>サーバーが textDocument/foldingRange をサポートしているか。InitializeAsync 後に確定する。</summary>
    bool SupportsFoldingRange { get; }
    /// <summary>サーバーが workspace/symbol をサポートしているか。InitializeAsync 後に確定する。</summary>
    bool SupportsWorkspaceSymbol { get; }
    /// <summary>サーバーが textDocument/rangeFormatting をサポートしているか。InitializeAsync 後に確定する。</summary>
    bool SupportsRangeFormatting { get; }
    /// <summary>サーバーが textDocument/semanticTokens/full をサポートしているか。InitializeAsync 後に確定する。</summary>
    bool SupportsSemanticTokens { get; }
    /// <summary>サーバーが textDocument/selectionRange をサポートしているか。InitializeAsync 後に確定する。</summary>
    bool SupportsSelectionRange { get; }
    /// <summary>サーバーが textDocument/diagnostic をサポートしているか。InitializeAsync 後に確定する。
    /// 既存の実装を壊さないよう既定は false。</summary>
    bool SupportsDocumentDiagnostics => false;
    /// <summary>サーバーが workspace/diagnostic をサポートしているか。InitializeAsync 後に確定する。</summary>
    bool SupportsWorkspaceDiagnostics { get; }
    /// <summary>セマンティックトークンの凡例（トークン種別・修飾子）。InitializeAsync 後に確定する。</summary>
    SemanticTokensLegend? SemanticTokensLegend { get; }
    event EventHandler<DiagnosticsChangedEventArgs>? DiagnosticsChanged;

    /// <summary>プロセス/接続が <see cref="IDisposable.Dispose"/> 以外の理由で死んだとき1回だけ発火する。
    /// ホスト側のプールがこれを見て作り直す。</summary>
    event Action? Exited;

    Task InitializeAsync(string rootUri);

    /// <summary>マルチルート初期化。ホストが実フォルダー一覧を持つ場合は全件渡す
    /// （<c>--autoLoadProjects</c> の Roslyn 等は rootUri ではなく workspaceFolders を見る）。</summary>
    Task InitializeAsync(string rootUri, IReadOnlyList<string>? workspaceFolderPaths);
    Task OpenDocumentAsync(string uri, string languageId, string text);
    Task ChangeDocumentAsync(string uri, int version, string text);
    Task CloseDocumentAsync(string uri);
    Task<IReadOnlyList<LspCompletionItem>> GetCompletionAsync(string uri, LspPosition position, CancellationToken ct = default);
    Task<LspHover?> GetHoverAsync(string uri, LspPosition position, CancellationToken ct = default);
    Task<(string Uri, int Line, int Column)?> GetDefinitionAsync(string uri, LspPosition position, CancellationToken ct = default);
    Task<LspSignatureHelp?> GetSignatureHelpAsync(string uri, LspPosition position, CancellationToken ct = default);
    Task<IReadOnlyList<LspTextEdit>> GetFormattingEditsAsync(string uri, int tabSize, bool insertSpaces, CancellationToken ct = default);
    Task<IReadOnlyList<LspTextEdit>> GetRangeFormattingEditsAsync(string uri, LspRange range, int tabSize, bool insertSpaces, CancellationToken ct = default);
    Task<LspWorkspaceEdit?> GetRenameAsync(string uri, LspPosition position, string newName, CancellationToken ct = default);
    Task<IReadOnlyList<LspLocation>> GetReferencesAsync(string uri, LspPosition position, bool includeDeclaration = true, CancellationToken ct = default);
    Task<IReadOnlyList<LspFoldingRange>> GetFoldingRangesAsync(string uri, CancellationToken ct = default);
    Task<IReadOnlyList<LspSymbolInformation>> GetWorkspaceSymbolsAsync(string query, CancellationToken ct = default);
    Task<IReadOnlyList<DocumentSymbol>> GetDocumentSymbolsAsync(string uri, CancellationToken ct = default);
    Task<IReadOnlyList<LspCodeAction>> GetCodeActionsAsync(string uri, LspRange range, CancellationToken ct = default);
    Task<IReadOnlyList<InlayHint>> GetInlayHintsAsync(string uri, LspRange range, CancellationToken ct = default);
    Task<SemanticToken[]?> GetSemanticTokensAsync(string uri, CancellationToken ct = default);
    /// <summary>プル型診断 (textDocument/diagnostic) を1ファイル分取得する。
    /// null は「取得できなかった」＝未サポート・エラー応答・例外のいずれか。この場合は既存の診断を
    /// そのまま残すこと（消してはならない）。取得できた場合は
    /// <see cref="LspDocumentDiagnosticReport.Unchanged"/> が true なら前回の診断を維持、
    /// false なら <see cref="LspDocumentDiagnosticReport.Diagnostics"/>（空もあり得る）で置き換える。
    /// 既定実装は null＝未対応。</summary>
    Task<LspDocumentDiagnosticReport?> GetDocumentDiagnosticsAsync(string uri, CancellationToken ct = default)
        => Task.FromResult<LspDocumentDiagnosticReport?>(null);
    Task<LspWorkspaceDiagnosticResult?> GetWorkspaceDiagnosticsAsync(CancellationToken ct = default);

    // Call hierarchy
    Task<CallHierarchyItem?> PrepareCallHierarchyAsync(string uri, LspPosition pos, CancellationToken ct = default);
    Task<CallHierarchyIncomingCall[]?> GetIncomingCallsAsync(CallHierarchyItem item, CancellationToken ct = default);
    Task<CallHierarchyOutgoingCall[]?> GetOutgoingCallsAsync(CallHierarchyItem item, CancellationToken ct = default);

    // Type hierarchy
    Task<TypeHierarchyItem?> PrepareTypeHierarchyAsync(string uri, LspPosition pos, CancellationToken ct = default);
    Task<TypeHierarchyItem[]?> GetSupertypesAsync(TypeHierarchyItem item, CancellationToken ct = default);
    Task<TypeHierarchyItem[]?> GetSubtypesAsync(TypeHierarchyItem item, CancellationToken ct = default);

    // Document highlight
    Task<IReadOnlyList<DocumentHighlight>?> RequestDocumentHighlightAsync(string uri, int line, int character, CancellationToken ct = default);

    // Selection range
    Task<IReadOnlyList<LspSelectionRange>?> RequestSelectionRangesAsync(string uri, IReadOnlyList<LspPosition> positions, CancellationToken ct = default);
}
