namespace Editor.Core.Lsp;

public class DiagnosticsChangedEventArgs(string uri, IReadOnlyList<LspDiagnostic> diagnostics) : EventArgs
{
    public string Uri { get; } = uri;
    public IReadOnlyList<LspDiagnostic> Diagnostics { get; } = diagnostics;
}

/// <summary>サーバー起点の <c>workspace/applyEdit</c>。ホストが実際に文書へ適用し、
/// <see cref="Applied"/> をその結果に設定して返す（サーバーはこの真偽で後続の動作を変える）。
///
/// <para><b>LSP の読み取りスレッドで発火し、ハンドラが戻るまでサーバーは待つ。</b>
/// 抽出リファクタリングを <c>workspace/executeCommand</c> で実行するサーバー（tsserver 系）は
/// この経路でしか編集を返さないので、未処理＝リファクタリングが無言で失敗する。</para></summary>
public sealed class LspApplyEditEventArgs(LspWorkspaceEdit edit, string? label) : EventArgs
{
    public LspWorkspaceEdit Edit { get; } = edit;
    /// <summary>サーバーが付けた操作名（Undo の見出しに使える）。</summary>
    public string? Label { get; } = label;
    public bool Applied { get; set; }
    public string? FailureReason { get; set; }
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
    /// <summary>サーバーが textDocument/semanticTokens/full/delta をサポートしているか。InitializeAsync 後に確定する。</summary>
    bool SupportsSemanticTokensDelta => false;
    /// <summary>サーバーが textDocument/selectionRange をサポートしているか。InitializeAsync 後に確定する。</summary>
    bool SupportsSelectionRange { get; }
    /// <summary>サーバーが textDocument/diagnostic をサポートしているか。InitializeAsync 後に確定する。
    /// 既存の実装を壊さないよう既定は false。</summary>
    bool SupportsDocumentDiagnostics => false;
    /// <summary>サーバーが workspace/diagnostic をサポートしているか。InitializeAsync 後に確定する。</summary>
    bool SupportsWorkspaceDiagnostics { get; }
    /// <summary>initializeのcompletionProvider.triggerCharacters。未申告サーバー向け既定は'.'。</summary>
    IReadOnlyList<string> CompletionTriggerCharacters => ["."];
    bool SupportsCompletionResolve => false;
    bool SupportsImplementation => false;
    bool SupportsTypeDefinition => false;
    bool SupportsDeclaration => false;
    bool SupportsPrepareRename => false;
    bool SupportsDocumentHighlight => false;
    bool SupportsDocumentLink => false;
    bool SupportsCodeLens => false;
    bool SupportsCodeLensResolve => false;
    /// <summary>セマンティックトークンの凡例（トークン種別・修飾子）。InitializeAsync 後に確定する。</summary>
    SemanticTokensLegend? SemanticTokensLegend { get; }
    /// <summary>サーバーが申告した code action kind の一覧。空は「申告なし」で、
    /// この場合 <c>only</c> を絞っても意味がある保証はない（全件取得して自前で分類する）。
    /// InitializeAsync 後に確定する。</summary>
    IReadOnlyList<string> CodeActionKinds => [];
    /// <summary>サーバーが <c>codeAction/resolve</c> をサポートしているか。InitializeAsync 後に確定する。
    /// Roslyn はリファクタリングの edit を必ず解決時に作るため、これが false のサーバーとは
    /// 挙動が根本的に違う。</summary>
    bool SupportsCodeActionResolve => false;
    /// <summary>サーバーが <c>workspace/executeCommand</c> で受け付けるコマンド名。
    /// 空は未申告＝コマンド型 code action は実行できないものとして扱う。</summary>
    IReadOnlyList<string> ExecuteCommandNames => [];

    event EventHandler<DiagnosticsChangedEventArgs>? DiagnosticsChanged;

    /// <summary>サーバー起点の <c>workspace/applyEdit</c>。<see cref="LspApplyEditEventArgs"/> 参照。</summary>
    event EventHandler<LspApplyEditEventArgs>? ApplyEditRequested;

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
    Task<LspCompletionItem?> ResolveCompletionAsync(LspCompletionItem item, CancellationToken ct = default)
        => Task.FromResult<LspCompletionItem?>(null);
    Task<LspHover?> GetHoverAsync(string uri, LspPosition position, CancellationToken ct = default);
    Task<(string Uri, int Line, int Column)?> GetDefinitionAsync(string uri, LspPosition position, CancellationToken ct = default);
    Task<IReadOnlyList<LspLocation>> GetImplementationAsync(string uri, LspPosition position, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<LspLocation>>([]);
    Task<IReadOnlyList<LspLocation>> GetTypeDefinitionAsync(string uri, LspPosition position, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<LspLocation>>([]);
    Task<IReadOnlyList<LspLocation>> GetDeclarationAsync(string uri, LspPosition position, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<LspLocation>>([]);
    Task<LspRange?> PrepareRenameAsync(string uri, LspPosition position, CancellationToken ct = default)
        => Task.FromResult<LspRange?>(null);
    Task<LspSignatureHelp?> GetSignatureHelpAsync(string uri, LspPosition position, CancellationToken ct = default);
    Task<IReadOnlyList<LspTextEdit>> GetFormattingEditsAsync(string uri, int tabSize, bool insertSpaces, CancellationToken ct = default);
    Task<IReadOnlyList<LspTextEdit>> GetRangeFormattingEditsAsync(string uri, LspRange range, int tabSize, bool insertSpaces, CancellationToken ct = default);
    Task<LspWorkspaceEdit?> GetRenameAsync(string uri, LspPosition position, string newName, CancellationToken ct = default);
    Task<IReadOnlyList<LspLocation>> GetReferencesAsync(string uri, LspPosition position, bool includeDeclaration = true, CancellationToken ct = default);
    Task<IReadOnlyList<LspFoldingRange>> GetFoldingRangesAsync(string uri, CancellationToken ct = default);
    Task<IReadOnlyList<LspSymbolInformation>> GetWorkspaceSymbolsAsync(string query, CancellationToken ct = default);
    Task<IReadOnlyList<DocumentSymbol>> GetDocumentSymbolsAsync(string uri, CancellationToken ct = default);
    Task<IReadOnlyList<LspCodeAction>> GetCodeActionsAsync(string uri, LspRange range, CancellationToken ct = default);

    /// <summary>範囲と kind を指定して code action を取得する。
    /// <paramref name="only"/> は <c>context.only</c>（例: <c>["refactor"]</c>）で、
    /// リファクタリング一覧に quick fix を混ぜないために使う。null なら絞り込まない。
    /// <paramref name="diagnostics"/> は <c>context.diagnostics</c>——quick fix を要求するときは
    /// 対象の診断を渡さないと候補を出さないサーバーがある。
    /// 既定実装は位置版へ委譲する（未対応クライアント向けの劣化動作）。</summary>
    Task<IReadOnlyList<LspCodeAction>> GetCodeActionsAsync(
        string uri, LspRange range, IReadOnlyList<string>? only,
        IReadOnlyList<LspDiagnostic>? diagnostics, CancellationToken ct = default)
        => GetCodeActionsAsync(uri, range, ct);

    /// <summary><c>codeAction/resolve</c> で edit を確定させる。解決できなければ null。
    /// <see cref="LspCodeAction.RawJson"/> を持たないアクションは解決できない。</summary>
    Task<LspCodeAction?> ResolveCodeActionAsync(LspCodeAction action, CancellationToken ct = default)
        => Task.FromResult<LspCodeAction?>(null);

    /// <summary><c>workspace/executeCommand</c> でコマンド型 code action を実行する。
    /// 編集はこの応答ではなく、サーバー起点の <see cref="ApplyEditRequested"/> で返ってくる。</summary>
    Task<bool> ExecuteCommandAsync(LspCodeActionCommand command, CancellationToken ct = default)
        => Task.FromResult(false);
    Task<IReadOnlyList<InlayHint>> GetInlayHintsAsync(string uri, LspRange range, CancellationToken ct = default);
    Task<SemanticToken[]?> GetSemanticTokensAsync(string uri, CancellationToken ct = default);
    /// <summary>Semantic tokens with the optional result id used for incremental refreshes.
    /// The default implementation preserves compatibility with hosts that only provide full tokens.</summary>
    async Task<SemanticTokensResult?> GetSemanticTokensResultAsync(
        string uri, string? previousResultId, CancellationToken ct = default)
    {
        var tokens = await GetSemanticTokensAsync(uri, ct);
        return tokens is null ? null : new SemanticTokensResult(null, tokens);
    }
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
    Task<IReadOnlyList<LspDocumentLink>> GetDocumentLinksAsync(string uri, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<LspDocumentLink>>([]);
    Task<IReadOnlyList<LspCodeLens>> GetCodeLensesAsync(string uri, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<LspCodeLens>>([]);
    Task<LspCodeLens?> ResolveCodeLensAsync(LspCodeLens lens, CancellationToken ct = default)
        => Task.FromResult<LspCodeLens?>(null);

    // Selection range
    Task<IReadOnlyList<LspSelectionRange>?> RequestSelectionRangesAsync(string uri, IReadOnlyList<LspPosition> positions, CancellationToken ct = default);
}
